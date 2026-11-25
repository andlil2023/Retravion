using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.AuditTrail;

namespace Retravion.Infrastructure.Specification;

public sealed class PostgresSpecificationWriteStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null,
    Func<NpgsqlConnection, string, CancellationToken, Task<Maybe<string>>>? findActiveUsername = null) : ISpecificationWriteStore
{
    private const string ProductIdIndex = "specification_product_id_unique";
    private const string SubmitForReviewAction = "specification.submit-for-review";
    private const string ReturnToDraftAction = "specification.return-to-draft";

    public Task<Result<DraftSpecification>> Create(DraftSpecification draft, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
        Execute(draft.ProductInternalId, authorized, async (connection, transaction) =>
        {
            if (draft.Links.Targets.Count != 0)
                return Result.Failure<DraftSpecification>("Set links after creating the Draft Specification.");
            await using var insert = new NpgsqlCommand("""
                INSERT INTO retravion_specification.specification (internal_id, product_internal_id, id, title, description, status)
                VALUES ($1, $2, $3, $4, $5, 'Draft')
                """, connection, transaction);
            insert.Parameters.AddWithValue(draft.InternalId);
            insert.Parameters.AddWithValue(draft.ProductInternalId);
            insert.Parameters.AddWithValue(draft.Id);
            insert.Parameters.AddWithValue(draft.Title);
            insert.Parameters.AddWithValue(draft.Description);
            var saved = await SaveContent(insert, cancellationToken);
            if (saved.IsFailure) return Result.Failure<DraftSpecification>(saved.Error);

            await AuditChange(connection, transaction, actor, occurredAt, "specification.create", draft.InternalId, null,
                Snapshot(draft), cancellationToken);
            return Result.Success(draft);
        }, cancellationToken);

    public Task<Result<DraftSpecification>> EditContent(Guid productInternalId, DraftSpecification expected, string? id, string? title,
        string? description, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var internalId = expected.InternalId;
            var current = await LockDraft(connection, transaction, productInternalId, internalId, "content", cancellationToken);
            if (current.IsFailure) return current;
            if ((expected.Id, expected.Title, expected.Description) != (current.Value.Id, current.Value.Title, current.Value.Description))
                return Result.Failure<DraftSpecification>("Draft content changed. Reload and review the current Draft before saving.");
            var before = Snapshot(current.Value);
            var edit = current.Value.EditContent(id, title, description);
            if (edit.IsFailure) return edit;
            var after = Snapshot(edit.Value);
            if (before == after) return edit;

            await using var update = new NpgsqlCommand("""
                UPDATE retravion_specification.specification SET id = $3, title = $4, description = $5
                WHERE product_internal_id = $1 AND internal_id = $2 AND status = 'Draft'
                """, connection, transaction);
            update.Parameters.AddWithValue(productInternalId);
            update.Parameters.AddWithValue(internalId);
            update.Parameters.AddWithValue(after.Id);
            update.Parameters.AddWithValue(after.Title);
            update.Parameters.AddWithValue(after.Description);
            var saved = await SaveContent(update, cancellationToken);
            if (saved.IsFailure) return Result.Failure<DraftSpecification>(saved.Error);

            var reviewChange = await InvalidateAiReview(connection, transaction, internalId, cancellationToken);
            if (reviewChange.IsFailure) return Result.Failure<DraftSpecification>(reviewChange.Error);
            await AuditChange(connection, transaction, actor, occurredAt, "specification.edit-content", internalId, before, after,
                cancellationToken, aiReview: reviewChange.Value.GetValueOrDefault());
            return edit;
        }, cancellationToken);

    public Task<Result<DraftSpecification>> EditLinks(Guid productInternalId, Guid internalId, IEnumerable<SpecificationLink> targets,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockDraft(connection, transaction, productInternalId, internalId, "links", cancellationToken);
            if (current.IsFailure) return current;
            var edit = current.Value.EditLinks(targets);
            if (edit.IsFailure || current.Value.Links.Equals(edit.Value.Links)) return edit;

            foreach (var target in edit.Value.Links.Targets)
            {
                var found = await PostgresSpecificationVersionSource.For(target.Kind)
                    .Resolve(connection, transaction, productInternalId, target.VersionId, cancellationToken);
                if (found.HasNoValue)
                    return Result.Failure<DraftSpecification>($"The selected {target.Kind} version was not found in this Product.");
            }

            await using (var remove = new NpgsqlCommand("""
                DELETE FROM retravion_specification.link WHERE specification_internal_id = $1
                """, connection, transaction))
            {
                remove.Parameters.AddWithValue(internalId);
                await remove.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO retravion_specification.link (specification_internal_id, kind, version_id)
                SELECT $1, kind, version_id FROM unnest($2::smallint[], $3::uuid[]) AS targets(kind, version_id)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue(internalId);
                insert.Parameters.AddWithValue(edit.Value.Links.Targets.Select(link => (short)link.Kind).ToArray());
                insert.Parameters.AddWithValue(edit.Value.Links.Targets.Select(link => link.VersionId).ToArray());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) != edit.Value.Links.Targets.Count)
                    return Result.Failure<DraftSpecification>("The Specification links could not be saved.");
            }

            var reviewChange = await InvalidateAiReview(connection, transaction, internalId, cancellationToken);
            if (reviewChange.IsFailure) return Result.Failure<DraftSpecification>(reviewChange.Error);
            await AuditChange(connection, transaction, actor, occurredAt, "specification.edit-links", internalId,
                LinkSnapshot(current.Value), LinkSnapshot(edit.Value), cancellationToken, aiReview: reviewChange.Value.GetValueOrDefault());
            return edit;
        }, cancellationToken);

    public async Task<Result> ChangeAssignment(Guid productInternalId, Guid internalId, string? assignee,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        await Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockSpecification<DevelopmentSpecification>(connection, transaction, productInternalId, internalId,
                status => status is "Draft" or "In Review" or "Approved", "Assignment cannot be changed in this Specification state.",
                cancellationToken);
            if (current.IsFailure) return Result.Failure<bool>(current.Error);

            var oldAssignee = await ReadAssignee(connection, transaction, internalId, cancellationToken);
            string? newAssignee = null;
            if (assignee is not null)
            {
                const string invalid = "Select an active user for the Specification assignment.";
                if (string.IsNullOrWhiteSpace(assignee) || assignee.Contains('\0')) return Result.Failure<bool>(invalid);
                try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(assignee); }
                catch (System.Text.EncoderFallbackException) { return Result.Failure<bool>(invalid); }

                if (oldAssignee is not null && oldAssignee.ToUpperInvariant() == assignee.ToUpperInvariant())
                    return Result.Success(false);
                if (findActiveUsername is null) throw new InvalidOperationException("Specification assignment requires an Identity lookup.");
                var found = await findActiveUsername(connection, assignee, cancellationToken);
                if (found.HasNoValue) return Result.Failure<bool>(invalid);
                newAssignee = found.Value;
            }

            var change = await RecordAssignmentChange(connection, transaction, internalId, actor, occurredAt,
                oldAssignee, newAssignee, cancellationToken);
            if (change.HasNoValue) return Result.Success(false);
            var snapshot = Snapshot(current.Value, current.Value switch
            {
                DraftSpecification => "Draft",
                InReviewSpecification => "In Review",
                ApprovedSpecification => "Approved",
                _ => throw new InvalidOperationException("Unexpected Specification assignment state.")
            });
            await AuditChange(connection, transaction, actor, occurredAt, "specification.change-assignment", internalId,
                snapshot, snapshot, cancellationToken, assignment: change.Value);
            return Result.Success(true);
        }, cancellationToken);

    // The caller holds the Specification row lock through reading and appending to the ledger.
    private static async Task<string?> ReadAssignee(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid internalId, CancellationToken token)
    {
        await using var read = new NpgsqlCommand("""
            SELECT new_assignee FROM retravion_specification.assignment_history
            WHERE specification_internal_id = $1 ORDER BY sequence DESC LIMIT 1
            """, connection, transaction);
        read.Parameters.AddWithValue(internalId);
        return await read.ExecuteScalarAsync(token) as string;
    }

    // Used for manual changes and available to finalization after it locks the row and reads the current assignee.
    // The caller includes the returned values in durable audit within this same transaction.
    private static async Task<Maybe<SpecificationAuditAssignment>> RecordAssignmentChange(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid internalId, AuditActor.Identified actor, DateTimeOffset occurredAt,
        string? oldAssignee, string? newAssignee, CancellationToken token)
    {
        if (oldAssignee == newAssignee) return Maybe<SpecificationAuditAssignment>.None;
        await using var insert = new NpgsqlCommand("""
            INSERT INTO retravion_specification.assignment_history
                (specification_internal_id, actor, occurred_at_utc_ticks, old_assignee, new_assignee)
            VALUES ($1, $2, $3, $4, $5)
            """, connection, transaction);
        insert.Parameters.AddWithValue(internalId);
        insert.Parameters.AddWithValue(actor.Identifier);
        insert.Parameters.AddWithValue(occurredAt.UtcTicks);
        insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)oldAssignee ?? DBNull.Value);
        insert.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)newAssignee ?? DBNull.Value);
        if (await insert.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("The Specification assignment history could not be saved.");
        return Maybe<SpecificationAuditAssignment>.From(new SpecificationAuditAssignment(oldAssignee, newAssignee));
    }

    public Task<Result<SpecificationAiReview>> SubmitAiReview(Guid productInternalId, Guid internalId, SpecificationAiReviewInput input,
        bool userApproved, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockSpecification<DevelopmentSpecification>(connection, transaction, productInternalId, internalId,
                status => status is "Draft" or "In Review", "AI reviews can only be submitted in Draft or In Review.", cancellationToken);
            if (current.IsFailure) return Result.Failure<SpecificationAiReview>(current.Error);
            var submitted = input switch
            {
                SpecificationAiReviewInput.Entered entered => SpecificationAiReview.Submit(entered.Markdown, userApproved),
                SpecificationAiReviewInput.Uploaded uploaded => SpecificationAiReview.SubmitUtf8(uploaded.Utf8Content.Span, userApproved),
                _ => Result.Failure<SpecificationAiReview>("Supply entered or uploaded Markdown for the Specification AI review.")
            };
            if (submitted.IsFailure) return submitted;

            var previous = await ReadAiReview(connection, transaction, internalId, cancellationToken);
            if (previous.IsFailure) return Result.Failure<SpecificationAiReview>(previous.Error);
            var before = previous.Value.GetValueOrDefault();
            var after = submitted.Value;
            if (before == after) return submitted;

            await using var save = new NpgsqlCommand("""
                INSERT INTO retravion_specification.ai_review (specification_internal_id, markdown, is_outdated)
                VALUES ($1, $2, false)
                ON CONFLICT (specification_internal_id) DO UPDATE SET markdown = EXCLUDED.markdown, is_outdated = false
                """, connection, transaction);
            save.Parameters.AddWithValue(internalId);
            save.Parameters.AddWithValue(after.Markdown);
            if (await save.ExecuteNonQueryAsync(cancellationToken) != 1)
                return Result.Failure<SpecificationAiReview>("The Specification AI review could not be saved.");

            var snapshot = Snapshot(current.Value, current.Value is DraftSpecification ? "Draft" : "In Review");
            await AuditChange(connection, transaction, actor, occurredAt, "specification.submit-ai-review", internalId,
                snapshot, snapshot, cancellationToken, aiReview: AiReviewChange(before, after, userApproved));
            return submitted;
        }, cancellationToken);

    // Every review writer holds the parent Specification row lock, including when no review exists yet.
    private static async Task<Result<Maybe<SpecificationAiReview>>> ReadAiReview(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid internalId, CancellationToken token)
    {
        await using var read = new NpgsqlCommand("""
            SELECT markdown, is_outdated FROM retravion_specification.ai_review WHERE specification_internal_id = $1
            """, connection, transaction);
        read.Parameters.AddWithValue(internalId);
        await using var reader = await read.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? PostgresSpecificationReadStore.MapAiReview(reader, 0).Map(Maybe<SpecificationAiReview>.From)
            : Result.Success(Maybe<SpecificationAiReview>.None);
    }

    private static async Task<Result<Maybe<SpecificationAuditAiReviewChange>>> InvalidateAiReview(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid internalId, CancellationToken token)
    {
        var previous = await ReadAiReview(connection, transaction, internalId, token);
        if (previous.IsFailure) return Result.Failure<Maybe<SpecificationAuditAiReviewChange>>(previous.Error);
        if (previous.Value.GetValueOrDefault() is not { IsOutdated: false } before)
            return Result.Success(Maybe<SpecificationAuditAiReviewChange>.None);

        await using var update = new NpgsqlCommand("""
            UPDATE retravion_specification.ai_review SET is_outdated = true WHERE specification_internal_id = $1
            """, connection, transaction);
        update.Parameters.AddWithValue(internalId);
        if (await update.ExecuteNonQueryAsync(token) != 1)
            return Result.Failure<Maybe<SpecificationAuditAiReviewChange>>("The Specification AI review could not be marked outdated.");
        return Result.Success(Maybe<SpecificationAuditAiReviewChange>.From(AiReviewChange(before, before.MarkOutdated())));
    }

    private static SpecificationAuditAiReviewChange AiReviewChange(SpecificationAiReview? before, SpecificationAiReview after,
        bool? userApproved = null) => new(before is null ? null : new(before.Markdown, before.IsOutdated),
        new(after.Markdown, after.IsOutdated), userApproved);

    public Task<Result<SpecificationEvidence>> SaveEvidence(Guid productInternalId, Guid internalId, SpecificationEvidence? expected, string? markdown,
        string? pullRequestUrl, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        ChangeEvidence(productInternalId, internalId, actor, occurredAt, authorized, "specification.save-evidence", before =>
        {
            if (expected != before) return Result.Failure<SpecificationEvidence>("Evidence changed. Review the refreshed evidence before saving.");
            var prepared = SpecificationEvidence.Prepare(markdown, pullRequestUrl);
            return before is not null && prepared.IsSuccess &&
                before.Markdown == prepared.Value.Markdown && before.PullRequestUrl == prepared.Value.PullRequestUrl
                ? Result.Success(before)
                : prepared;
        }, cancellationToken);

    public Task<Result<SpecificationEvidence>> ConfirmEvidenceCurrent(Guid productInternalId, Guid internalId, SpecificationEvidence? reviewedEvidence,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        ChangeEvidence(productInternalId, internalId, actor, occurredAt, authorized, "specification.confirm-evidence-current",
            before => before is null
                ? Result.Failure<SpecificationEvidence>("There is no retained Specification evidence to confirm.")
                : reviewedEvidence is not null && reviewedEvidence != before
                    ? Result.Failure<SpecificationEvidence>("Evidence changed. Review the refreshed evidence before confirming.")
                    : before.ConfirmCurrent(reviewedEvidence is not null), cancellationToken);

    private Task<Result<SpecificationEvidence>> ChangeEvidence(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, string action,
        Func<SpecificationEvidence?, Result<SpecificationEvidence>> change, CancellationToken token) =>
        Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockSpecification<ApprovedSpecification>(connection, transaction, productInternalId, internalId,
                status => status == "Approved", "Evidence can only be prepared or confirmed in Approved Specifications.", token);
            if (current.IsFailure) return Result.Failure<SpecificationEvidence>(current.Error);
            var previous = await ReadEvidence(connection, transaction, internalId, token);
            if (previous.IsFailure) return Result.Failure<SpecificationEvidence>(previous.Error);
            var before = previous.Value.GetValueOrDefault();
            var updated = change(before);
            if (updated.IsFailure || updated.Value == before) return updated;

            var after = updated.Value;
            var saved = await SaveEvidenceRow(connection, transaction, internalId, after, token);
            if (saved.IsFailure) return Result.Failure<SpecificationEvidence>(saved.Error);
            var snapshot = Snapshot(current.Value, "Approved");
            await AuditChange(connection, transaction, actor, occurredAt, action, internalId, snapshot, snapshot, token,
                evidence: EvidenceChange(before, after));
            return updated;
        }, token);

    // Evidence changes and reopen all hold the parent Specification row lock, even when no evidence exists yet.
    private static async Task<Result<Maybe<SpecificationEvidence>>> ReadEvidence(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid internalId, CancellationToken token)
    {
        await using var read = new NpgsqlCommand("""
            SELECT markdown, pull_request_url, is_outdated FROM retravion_specification.evidence WHERE specification_internal_id = $1
            """, connection, transaction);
        read.Parameters.AddWithValue(internalId);
        await using var reader = await read.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? PostgresSpecificationReadStore.MapEvidence(reader, 0).Map(Maybe<SpecificationEvidence>.From)
            : Result.Success(Maybe<SpecificationEvidence>.None);
    }

    private static async Task<Result> SaveEvidenceRow(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid internalId,
        SpecificationEvidence evidence, CancellationToken token)
    {
        await using var save = new NpgsqlCommand("""
            INSERT INTO retravion_specification.evidence (specification_internal_id, markdown, pull_request_url, is_outdated)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (specification_internal_id) DO UPDATE SET markdown = EXCLUDED.markdown,
                pull_request_url = EXCLUDED.pull_request_url, is_outdated = EXCLUDED.is_outdated
            """, connection, transaction);
        save.Parameters.AddWithValue(internalId);
        save.Parameters.AddWithValue(evidence.Markdown);
        save.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)evidence.PullRequestUrl ?? DBNull.Value);
        save.Parameters.AddWithValue(evidence.IsOutdated);
        return await save.ExecuteNonQueryAsync(token) == 1
            ? Result.Success() : Result.Failure("The Specification evidence could not be saved.");
    }

    private static async Task<Result<Maybe<SpecificationAuditEvidenceChange>>> InvalidateEvidence(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid internalId, CancellationToken token)
    {
        var previous = await ReadEvidence(connection, transaction, internalId, token);
        if (previous.IsFailure) return Result.Failure<Maybe<SpecificationAuditEvidenceChange>>(previous.Error);
        if (previous.Value.GetValueOrDefault() is not { IsOutdated: false } before)
            return Result.Success(Maybe<SpecificationAuditEvidenceChange>.None);

        var after = before.MarkOutdated();
        var saved = await SaveEvidenceRow(connection, transaction, internalId, after, token);
        return saved.IsFailure ? Result.Failure<Maybe<SpecificationAuditEvidenceChange>>(saved.Error)
            : Result.Success(Maybe<SpecificationAuditEvidenceChange>.From(EvidenceChange(before, after)));
    }

    private static SpecificationAuditEvidenceChange EvidenceChange(SpecificationEvidence? before, SpecificationEvidence after) =>
        new(before is null ? null : new(before.Markdown, before.PullRequestUrl, before.IsOutdated),
            new(after.Markdown, after.PullRequestUrl, after.IsOutdated));

    public Task<Result<InReviewSpecification>> SubmitForReview(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
        Transition<DraftSpecification, InReviewSpecification>(productInternalId, internalId, actor, occurredAt, authorized,
            SubmitForReviewAction, "Draft", "In Review", "Only Draft Specifications can be submitted for review.",
            draft => draft.SubmitForReview(), null, cancellationToken);

    public Task<Result<DraftSpecification>> ReturnToDraft(Guid productInternalId, Guid internalId, string? reason,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Transition<InReviewSpecification, DraftSpecification>(productInternalId, internalId, actor, occurredAt, authorized,
            ReturnToDraftAction, "In Review", "Draft", "Only In Review Specifications can be returned to Draft.",
            review => review.ReturnToDraft(reason), reason, cancellationToken);

    public async Task<Result<SpecificationApproval>> Approve(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default)
    {
        var result = await Transition<InReviewSpecification, ApprovedSpecification>(productInternalId, internalId, actor,
            occurredAt, authorized, "specification.approve", "In Review", "Approved",
            "Only In Review Specifications can be approved.", review => Result.Success(review.Approve()), null,
            cancellationToken, validate: LockApprovedTargets);
        return result.Map(approved => new SpecificationApproval(approved,
            Array.AsReadOnly(Enum.GetValues<SpecificationLinkKind>()
                .Except(approved.Links.Targets.Select(target => target.Kind)).ToArray())));
    }

    public Task<Result<DraftSpecification>> Reopen(Guid productInternalId, Guid internalId, string? reason,
        bool noActiveOrUncertainRunAttested, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
        Transition<ApprovedSpecification, DraftSpecification>(productInternalId, internalId, actor, occurredAt, authorized,
            "specification.reopen", "Approved", "Draft", "Only Approved Specifications can be reopened.",
            approved => approved.Reopen(reason, noActiveOrUncertainRunAttested), reason, cancellationToken,
            noActiveOrUncertainRunAttested, invalidateRetainedReviewAndEvidence: true);

    public Task<Result<ImplementedSpecification>> Implement(Guid productInternalId, Guid internalId, bool mergeAttested,
        string attestedPullRequestUrl, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default) =>
        Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockSpecification<ApprovedSpecification>(connection, transaction, productInternalId, internalId,
                status => status == "Approved", "Only Approved Specifications can be implemented.", cancellationToken);
            if (current.IsFailure) return Result.Failure<ImplementedSpecification>(current.Error);
            var evidence = await ReadEvidence(connection, transaction, internalId, cancellationToken);
            if (evidence.IsFailure) return Result.Failure<ImplementedSpecification>(evidence.Error);
            var next = current.Value.Implement(evidence.Value.GetValueOrDefault(), mergeAttested, actor.Identifier, occurredAt);
            if (next.IsFailure) return next;
            if (!string.Equals(next.Value.Evidence.PullRequestUrl, attestedPullRequestUrl, StringComparison.Ordinal))
                return Result.Failure<ImplementedSpecification>("The implementation PR URL has changed. Review the current PR URL and attest again.");

            // Approved and Implemented both protect the same exact links; neither the links nor their owners change here.
            await using var update = new NpgsqlCommand("""
                UPDATE retravion_specification.specification
                SET status = 'Implemented', merge_attested_by = $3, merge_attested_at_utc_ticks = $4
                WHERE product_internal_id = $1 AND internal_id = $2 AND status = 'Approved'
                """, connection, transaction);
            update.Parameters.AddWithValue(productInternalId);
            update.Parameters.AddWithValue(internalId);
            update.Parameters.AddWithValue(next.Value.MergeAttestedBy);
            update.Parameters.AddWithValue(next.Value.MergeAttestedAt.UtcTicks);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                return Result.Failure<ImplementedSpecification>("The Specification state could not be saved.");

            var oldAssignee = await ReadAssignee(connection, transaction, internalId, cancellationToken);
            var assignment = await RecordAssignmentChange(connection, transaction, internalId, actor, occurredAt,
                oldAssignee, null, cancellationToken);
            // The audit envelope's actor and occurrence time identify this attestation.
            await AuditChange(connection, transaction, actor, occurredAt, "specification.implement", internalId,
                Snapshot(current.Value, "Approved"), Snapshot(next.Value, "Implemented"), cancellationToken,
                assignment: assignment.GetValueOrDefault(), mergeAttested: mergeAttested);
            return next;
        }, cancellationToken);

    public async Task<Result> Delete(Guid productInternalId, Guid internalId, Guid? confirmedInternalId,
        bool noActiveOrUncertainRunAttested, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default) =>
        await Execute(productInternalId, authorized, async (connection, transaction) =>
        {
            var current = await LockSpecification<DevelopmentSpecification>(connection, transaction, productInternalId, internalId,
                status => status != "Implemented", "Implemented Specifications cannot be deleted.", cancellationToken);
            if (current.IsFailure) return Result.Failure<bool>(current.Error);
            var specification = current.Value;
            var confirmation = specification.ConfirmDeletion(confirmedInternalId, noActiveOrUncertainRunAttested);
            if (confirmation.IsFailure) return Result.Failure<bool>(confirmation.Error);

            // Inventory all owned data while holding the parent row lock used by every Specification writer.
            var status = specification switch
            {
                DraftSpecification => "Draft",
                InReviewSpecification => "In Review",
                ApprovedSpecification => "Approved",
                _ => throw new InvalidOperationException("Unexpected Specification deletion state.")
            };
            var before = LinkSnapshot(specification, status);
            var history = await ReadAssignmentAuditHistory(connection, transaction, internalId, cancellationToken);
            var review = await ReadAiReview(connection, transaction, internalId, cancellationToken);
            if (review.IsFailure) return Result.Failure<bool>(review.Error);
            var evidence = await ReadEvidence(connection, transaction, internalId, cancellationToken);
            if (evidence.IsFailure) return Result.Failure<bool>(evidence.Error);
            var deletion = new SpecificationAuditDeletion(history,
                review.Value.GetValueOrDefault() is { } r ? new(r.Markdown, r.IsOutdated) : null,
                evidence.Value.GetValueOrDefault() is { } e ? new(e.Markdown, e.PullRequestUrl, e.IsOutdated) : null);

            // The owned links, assignment history, AI review and evidence cascade. Linked owner versions and
            // durable audit events have no cascading relationship to this Specification.
            await using var remove = new NpgsqlCommand("""
                DELETE FROM retravion_specification.specification WHERE product_internal_id = $1 AND internal_id = $2
                """, connection, transaction);
            remove.Parameters.AddWithValue(productInternalId);
            remove.Parameters.AddWithValue(internalId);
            if (await remove.ExecuteNonQueryAsync(cancellationToken) != 1)
                return Result.Failure<bool>("The Specification could not be deleted.");

            await AuditChange(connection, transaction, actor, occurredAt, "specification.delete", internalId, before, null,
                cancellationToken, noActiveOrUncertainRunAttested: noActiveOrUncertainRunAttested, deletion: deletion);
            return Result.Success(true);
        }, cancellationToken);

    private static async Task<SpecificationAuditAssignmentEntry[]> ReadAssignmentAuditHistory(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid internalId, CancellationToken token)
    {
        await using var read = new NpgsqlCommand("""
            SELECT sequence, actor, occurred_at_utc_ticks, old_assignee, new_assignee
            FROM retravion_specification.assignment_history WHERE specification_internal_id = $1 ORDER BY sequence
            """, connection, transaction);
        read.Parameters.AddWithValue(internalId);
        var history = new List<SpecificationAuditAssignmentEntry>();
        await using var reader = await read.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            history.Add(new(reader.GetInt64(0), reader.GetString(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return history.ToArray();
    }

    private static async Task<Result> LockApprovedTargets(NpgsqlConnection connection, NpgsqlTransaction transaction,
        InReviewSpecification review, CancellationToken token)
    {
        // SpecificationLinks orders by kind and exact UUID. Keep the same row lock used by each owner reopening guard.
        foreach (var target in review.Links.Targets)
        {
            var found = await PostgresSpecificationVersionSource.For(target.Kind)
                .Resolve(connection, transaction, review.ProductInternalId, target.VersionId, token, lockVersion: true);
            if (found.HasNoValue || !found.Value.IsApproved)
                return Result.Failure($"Every linked {target.Kind} version must be Approved in this Product.");
        }
        return Result.Success();
    }

    private Task<Result<TNext>> Transition<TCurrent, TNext>(Guid productId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, string action, string before, string after, string invalidState,
        Func<TCurrent, Result<TNext>> transition, string? reason, CancellationToken token,
        bool? noActiveOrUncertainRunAttested = null,
        Func<NpgsqlConnection, NpgsqlTransaction, TCurrent, CancellationToken, Task<Result>>? validate = null,
        bool invalidateRetainedReviewAndEvidence = false)
        where TCurrent : DevelopmentSpecification where TNext : DevelopmentSpecification =>
        Execute(productId, authorized, async (connection, transaction) =>
        {
            var current = await LockSpecification<TCurrent>(connection, transaction, productId, internalId, status => status == before, invalidState, token);
            if (current.IsFailure) return Result.Failure<TNext>(current.Error);
            if (validate is not null)
            {
                var valid = await validate(connection, transaction, current.Value, token);
                if (valid.IsFailure) return Result.Failure<TNext>(valid.Error);
            }
            var next = transition(current.Value);
            if (next.IsFailure) return next;

            await using var update = new NpgsqlCommand("""
                UPDATE retravion_specification.specification SET status = $3
                WHERE product_internal_id = $1 AND internal_id = $2 AND status = $4
                """, connection, transaction);
            update.Parameters.AddWithValue(productId);
            update.Parameters.AddWithValue(internalId);
            update.Parameters.AddWithValue(after);
            update.Parameters.AddWithValue(before);
            if (await update.ExecuteNonQueryAsync(token) != 1)
                return Result.Failure<TNext>("The Specification state could not be saved.");

            SpecificationAuditAiReviewChange? aiReview = null;
            SpecificationAuditEvidenceChange? evidence = null;
            if (invalidateRetainedReviewAndEvidence)
            {
                var reviewChange = await InvalidateAiReview(connection, transaction, internalId, token);
                if (reviewChange.IsFailure) return Result.Failure<TNext>(reviewChange.Error);
                aiReview = reviewChange.Value.GetValueOrDefault();
                var evidenceChange = await InvalidateEvidence(connection, transaction, internalId, token);
                if (evidenceChange.IsFailure) return Result.Failure<TNext>(evidenceChange.Error);
                evidence = evidenceChange.Value.GetValueOrDefault();
            }
            await AuditChange(connection, transaction, actor, occurredAt, action, internalId,
                Snapshot(current.Value, before), Snapshot(next.Value, after), token, reason, noActiveOrUncertainRunAttested,
                aiReview: aiReview, evidence: evidence);
            return next;
        }, token);

    private static Task<Result<DraftSpecification>> LockDraft(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid productId, Guid internalId, string field, CancellationToken token) =>
        LockSpecification<DraftSpecification>(connection, transaction, productId, internalId, status => status == "Draft",
            $"Only Draft Specification {field} can be edited.", token);

    private static async Task<Result<T>> LockSpecification<T>(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid productId, Guid internalId, Func<string, bool> acceptsState, string invalidState, CancellationToken token)
        where T : DevelopmentSpecification
    {
        await using (var locked = new NpgsqlCommand("""
            SELECT status FROM retravion_specification.specification
            WHERE product_internal_id = $1 AND internal_id = $2 FOR UPDATE
            """, connection, transaction))
        {
            locked.Parameters.AddWithValue(productId);
            locked.Parameters.AddWithValue(internalId);
            if (await locked.ExecuteScalarAsync(token) is not string status)
                return Result.Failure<T>("The Specification was not found in this Product.");
            if (!acceptsState(status)) return Result.Failure<T>(invalidState);
        }

        // Read child rows in a new statement after acquiring the lock, including any just-committed link edits.
        await using var read = new NpgsqlCommand(PostgresSpecificationReadStore.Select + " AND internal_id = $2", connection, transaction);
        read.Parameters.AddWithValue(productId);
        read.Parameters.AddWithValue(internalId);
        await using var reader = await read.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            var mapped = PostgresSpecificationReadStore.Map(reader);
            if (mapped.IsSuccess && mapped.Value is T current) return Result.Success(current);
        }
        return Result.Failure<T>("The persisted Specification is invalid.");
    }

    private static SpecificationAuditValues LinkSnapshot(DevelopmentSpecification specification, string status = "Draft") =>
        Snapshot(specification, status) with
        {
            Links = specification.Links.Targets.Select(link => new SpecificationAuditLink(link.Kind.ToString(), link.VersionId)).ToArray()
        };

    private static SpecificationAuditValues Snapshot(DraftSpecification draft) => Snapshot(draft, "Draft");

    private static SpecificationAuditValues Snapshot(DevelopmentSpecification specification, string status) =>
        new(specification.ProductInternalId, specification.Id, specification.Title, specification.Description, status);

    private static Task AuditChange(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor.Identified actor,
        DateTimeOffset occurredAt, string action, Guid internalId, SpecificationAuditValues? before, SpecificationAuditValues? after,
        CancellationToken cancellationToken, string? reason = null, bool? noActiveOrUncertainRunAttested = null,
        SpecificationAuditAssignment? assignment = null, SpecificationAuditAiReviewChange? aiReview = null,
        SpecificationAuditEvidenceChange? evidence = null, bool? mergeAttested = null, SpecificationAuditDeletion? deletion = null) =>
        PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(internalId.ToString()),
                AuditActionOutcome.Success, specificationChange: new SpecificationAuditDetails(internalId, before, after, reason,
                    noActiveOrUncertainRunAttested, assignment, aiReview, evidence, mergeAttested, deletion)),
            cancellationToken);

    private static async Task<Result> SaveContent(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                ? Result.Success() : Result.Failure("The Specification operation could not be completed.");
        }
        catch (PostgresException exception) when (!cancellationToken.IsCancellationRequested && IsUnindexableId(exception))
        {
            return Result.Failure("The Specification ID is too large to store.");
        }
    }

    // The ID is the only variable-width index key on a content write. PostgreSQL may reject
    // the tuple before the B-tree size check, in which case it does not report an index name.
    private static bool IsUnindexableId(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.ProgramLimitExceeded && exception is
            ({ Routine: "_bt_check_third_page", ConstraintName: ProductIdIndex } or { Routine: "index_form_tuple_context" });

    private async Task<Result<T>> Execute<T>(Guid productInternalId, Func<Task<bool>> authorized,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> change, CancellationToken cancellationToken)
    {
        if (!await authorized()) return Result.Failure<T>("Access denied.");
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            await PostgresSpecificationReadStore.EnsureSchema(connection, transaction, cancellationToken);

            // Product owns the active flag. Hold a row lock through commit so retirement cannot race a write.
            await using (var product = new NpgsqlCommand("""
                SELECT is_active FROM retravion_product.product WHERE internal_id = $1 FOR SHARE
                """, connection, transaction))
            {
                product.Parameters.AddWithValue(productInternalId);
                if (await product.ExecuteScalarAsync(cancellationToken) is not true)
                    return Result.Failure<T>("The responsible Product is retired or no longer exists.");
            }

            var result = await change(connection, transaction);
            if (result.IsFailure) return result;
            if (!await authorized()) return Result.Failure<T>("Access denied.");
            cancellationToken.ThrowIfCancellationRequested();
            commitStarted = true;
            if (commit is null) await transaction.CommitAsync(cancellationToken);
            else await commit(transaction, cancellationToken);
            return result;
        }
        catch (Exception) when (commitStarted)
        {
            return Result.Failure<T>("The Specification operation outcome could not be confirmed. Check the current records before retrying; do not retry automatically.");
        }
        catch (PostgresException exception) when (!cancellationToken.IsCancellationRequested &&
            exception.SqlState == PostgresErrorCodes.UniqueViolation && exception.ConstraintName == ProductIdIndex)
        {
            return Result.Failure<T>("That Specification ID is already in use in this Product.");
        }
        catch (NpgsqlException exception) when (!cancellationToken.IsCancellationRequested &&
            (exception.IsTransient || exception is PostgresException
            {
                SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction
            }))
        {
            return Result.Failure<T>("The Specification operation could not be completed.");
        }
        catch (NpgsqlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Surface unexpected faults after rollback without retaining provider details or submitted values.
            throw new InvalidOperationException("An unexpected Specification storage error occurred.");
        }
    }
}
