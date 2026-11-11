// Dependency-free bridge checks: node --test tests/TerminalBridge.Tests.mjs
// Fakes only: no network, certificate, browser or Retravion credential is used.
import assert from "node:assert/strict";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import { RetravionTerminal, connectionTools, createRequest, serve, validateEndpoint } from "../src/Retravion.Web/Features/Mcp/TerminalSkills/bridge/server.mjs";

const endpoint = "https://localhost:8443/mcp";
const deviceCode = "AB".repeat(32);
const remoteTools = [{ name: "list_products", description: "List", inputSchema: { type: "object" } }];
const json = (value, status = 200, headers = {}) => ({ status, headers: { "content-type": "application/json", ...headers }, text: JSON.stringify(value) });
const payload = response => JSON.parse(response.content[0].text);

function harness({ statuses = ["approved"], start, mcp, open = async () => true } = {}) {
	const calls = [], opened = [];
	const clock = { now: 0 };
	const request = async (url, options) => {
		calls.push({ path: url.pathname, authorization: options.headers.Authorization, body: options.body && JSON.parse(options.body), headers: options.headers });
		if (url.pathname === "/mcp-connect/start") return start?.() ?? json({ deviceCode, userCode: "ABCD-2345",
			verificationUri: "https://localhost:8443/settings/ai-workflow?connect=ABCD-2345", expiresIn: 600, interval: 2 });
		if (url.pathname === "/mcp-connect/status") return json({ status: statuses.length > 1 ? statuses.shift() : statuses[0] });
		if (url.pathname === "/mcp-connect/disconnect") return { status: 204, headers: {}, text: "" };
		const message = JSON.parse(options.body);
		if (message.id === undefined) return { status: 202, headers: {}, text: "" };
		if (mcp) return mcp(message);
		if (message.method === "initialize") return json({ jsonrpc: "2.0", id: message.id, result: {} }, 200, { "mcp-session-id": "session-1" });
		return { status: 200, headers: { "content-type": "text/event-stream" },
			text: `event: message\ndata: ${JSON.stringify({ jsonrpc: "2.0", id: message.id, result: { content: [{ type: "text", text: "[]" }] } })}\n\n` };
	};
	const terminal = new RetravionTerminal({ endpoint, request, tools: remoteTools, now: () => clock.now,
		sleep: async ms => { clock.now += ms; }, open: async url => { opened.push(url); return open(url); } });
	return { terminal, calls, opened, clock };
}

test("endpoint must be loopback HTTPS /mcp and a certificate is required", () => {
	assert.equal(validateEndpoint(endpoint).origin, "https://localhost:8443");
	for (const bad of ["http://localhost:8443/mcp", "https://example.com/mcp", "https://localhost:8443/other", "https://user:pw@localhost/mcp", "https://localhost/mcp?x=1", "", undefined])
		assert.throws(() => validateEndpoint(bad));
	assert.throws(() => createRequest(undefined), /mcp_setup/);
});

test("connect opens the browser and never returns the credential", async () => {
	const { terminal, opened } = harness();
	const response = await terminal.call("rtv_connect");
	assert.equal(response.isError, undefined);
	assert.deepEqual(opened, ["https://localhost:8443/settings/ai-workflow?connect=ABCD-2345"]);
	const value = payload(response);
	assert.equal(value.browserOpened, true);
	assert.equal(value.userCode, "ABCD-2345");
	assert.ok(!JSON.stringify(response).includes(deviceCode));
});

test("connect returns the link as fallback when the browser cannot be opened", async () => {
	const { terminal } = harness({ open: async () => false });
	const value = payload(await terminal.call("rtv_connect"));
	assert.equal(value.browserOpened, false);
	assert.equal(value.verificationUri, "https://localhost:8443/settings/ai-workflow?connect=ABCD-2345");
	assert.match(value.message, /show the user this link/);
});

test("connect refuses a verification link on another origin or path and malformed codes", async () => {
	const base = { deviceCode, userCode: "ABCD-2345", verificationUri: "https://localhost:8443/settings/ai-workflow?connect=ABCD-2345", expiresIn: 600 };
	for (const change of [{ verificationUri: "https://evil.invalid/settings/ai-workflow" }, { verificationUri: "https://localhost:9999/settings/ai-workflow" },
		{ verificationUri: "https://localhost:8443/other" }, { deviceCode: "short" }, { userCode: "<script>" }, { expiresIn: 0 }]) {
		const { terminal, opened } = harness({ start: () => json({ ...base, ...change }) });
		const response = await terminal.call("rtv_connect");
		assert.equal(response.isError, true);
		assert.deepEqual(opened, []);
		assert.equal((await terminal.call("rtv_complete_connection")).isError, true);
	}
});

test("connect reports disabled MCP or an unreachable application without details", async () => {
	for (const start of [() => json({}, 403), () => { throw new Error("self-signed certificate SECRET-DETAIL"); }]) {
		const { terminal } = harness({ start });
		const response = await terminal.call("rtv_connect");
		assert.equal(response.isError, true);
		assert.match(payload(response).error, /could not start.*MCP is enabled.*mcp_setup\.ps1/);
		assert.ok(!JSON.stringify(response).includes("SECRET-DETAIL"));
	}
});

test("complete polls until approved, then forwards tools with the bearer credential", async () => {
	const { terminal, calls } = harness({ statuses: ["pending", "pending", "approved"] });
	assert.equal((await terminal.call("list_products")).isError, true);
	assert.equal((await terminal.call("rtv_complete_connection")).isError, true);
	await terminal.call("rtv_connect");
	const completed = await terminal.call("rtv_complete_connection");
	assert.equal(payload(completed).isSuccess, true);
	assert.ok(!JSON.stringify(completed).includes(deviceCode));
	assert.equal(calls.filter(call => call.path === "/mcp-connect/status").length, 3);
	const listed = await terminal.call("list_products");
	assert.equal(listed.content[0].text, "[]");
	const forwarded = calls.at(-1);
	assert.equal(forwarded.path, "/mcp");
	assert.equal(forwarded.authorization, `Bearer ${deviceCode}`);
	assert.equal(forwarded.headers["Mcp-Session-Id"], "session-1");
	assert.deepEqual(forwarded.body.params, { name: "list_products", arguments: {} });
	assert.equal((await terminal.call("unknown_tool")).isError, true);
});

test("complete reports denial, expiry and a still-pending approval", async () => {
	for (const [status, expected] of [["denied", /denied/], ["expired", /expired/]]) {
		const { terminal } = harness({ statuses: [status] });
		await terminal.call("rtv_connect");
		const response = await terminal.call("rtv_complete_connection");
		assert.equal(response.isError, true);
		assert.match(payload(response).error, expected);
		assert.match(payload(await terminal.call("rtv_complete_connection")).error, /rtv_connect first/);
	}
	const { terminal, clock } = harness({ statuses: ["pending"] });
	await terminal.call("rtv_connect");
	assert.match(payload(await terminal.call("rtv_complete_connection")).error, /Still waiting/);
	clock.now = 601000;
	assert.match(payload(await terminal.call("rtv_complete_connection")).error, /expired/);
});

test("an ended connection drops the credential and asks for reconnect without retrying", async () => {
	for (const status of [401, 403, 404]) {
		let attempts = 0;
		const { terminal, calls } = harness({ mcp: message => message.method === "initialize"
			? json({ jsonrpc: "2.0", id: message.id, result: {} }) : (attempts++, json({}, status)) });
		await terminal.call("rtv_connect"); await terminal.call("rtv_complete_connection");
		const response = await terminal.call("list_products");
		assert.equal(response.isError, true);
		assert.match(payload(response).error, /rtv_connect again/);
		assert.equal(attempts, 1);
		const before = calls.length;
		assert.match(payload(await terminal.call("list_products")).error, /Not connected/);
		assert.equal(calls.length, before);
	}
});

test("a lost response is reported as uncertain and the tool error from Retravion is passed through", async () => {
	let mode = "lost";
	const { terminal } = harness({ mcp: message => {
		if (message.method === "initialize") return json({ jsonrpc: "2.0", id: message.id, result: {} });
		if (mode === "lost") throw new Error("socket hang up");
		return json({ jsonrpc: "2.0", id: message.id, result: { isError: true, content: [{ type: "text", text: "Stale revision." }] } });
	} });
	await terminal.call("rtv_connect"); await terminal.call("rtv_complete_connection");
	assert.match(payload(await terminal.call("list_products")).error, /outcome is unknown/);
	mode = "error";
	const refused = await terminal.call("list_products");
	assert.equal(refused.isError, true);
	assert.equal(refused.content[0].text, "Stale revision.");
});

test("disconnect ends server access and discards the local credential", async () => {
	const { terminal, calls } = harness();
	await terminal.call("rtv_connect"); await terminal.call("rtv_complete_connection");
	assert.equal(payload(await terminal.call("rtv_disconnect")).isSuccess, true);
	assert.deepEqual([calls.at(-1).path, calls.at(-1).authorization], ["/mcp-connect/disconnect", `Bearer ${deviceCode}`]);
	assert.match(payload(await terminal.call("list_products")).error, /Not connected/);
});

test("stdio transport lists connection and Retravion tools and answers calls", async () => {
	const { terminal } = harness();
	const input = new PassThrough(), output = new PassThrough();
	const lines = [];
	output.on("data", chunk => lines.push(...chunk.toString().split("\n").filter(Boolean).map(line => JSON.parse(line))));
	const served = serve(terminal, input, output);
	input.write(JSON.stringify({ jsonrpc: "2.0", id: 0, method: "tools/list" }) + "\n");
	input.write(JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: {} }) + "\n");
	input.write(JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" }) + "\n");
	input.write(JSON.stringify({ jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "list_products" } }) + "\n");
	input.write("not json\n");
	input.end();
	await served;
	assert.equal(lines.find(line => line.id === 0).error.code, -32000);
	assert.deepEqual(lines.find(line => line.id === 2).result.tools.map(tool => tool.name),
		[...connectionTools.map(tool => tool.name), "list_products"]);
	assert.equal(lines.find(line => line.id === 3).result.isError, true);
	assert.equal(lines.find(line => line.id === null).error.code, -32700);
});
