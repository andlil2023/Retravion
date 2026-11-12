// Retravion terminal bridge: a local stdio MCP server for Pi and Claude Code.
// It obtains browser approval, keeps the credential in this process only, and forwards the
// existing Retravion tools over HTTPS. No dependencies; requires Node.js 22 or later.
import { execFile } from "node:child_process";
import { readFileSync } from "node:fs";
import https from "node:https";
import { setTimeout as delay } from "node:timers/promises";
import { fileURLToPath, pathToFileURL } from "node:url";

const version = "2.5.0";
const protocolVersion = "2025-11-25";
const maxBytes = 16 * 1024 * 1024;
const loopback = ["localhost", "127.0.0.1", "[::1]"];
const ended = "Retravion connection ended (logout, 30 idle minutes, application restart, revocation or MCP disabled). " +
	"Ask the user, then call rtv_connect again. Never retry a write automatically: read the current state first.";

const object = (properties = {}) => ({ type: "object", properties, additionalProperties: false });
export const connectionTools = [
	{ name: "rtv_connect", description: "Start a Retravion connection. Ask the user for consent first. Opens the user's browser at Retravion for sign-in and approval; show the returned link and code as a fallback. Never ask for a password or token. Changes no record.", inputSchema: object() },
	{ name: "rtv_complete_connection", description: "Finish connecting after the user confirms they signed in and approved in the browser. The credential stays inside the bridge process.", inputSchema: object() },
	{ name: "rtv_disconnect", description: "End this terminal's Retravion access and discard the credential held by the bridge.", inputSchema: object() },
];

const result = value => ({ content: [{ type: "text", text: JSON.stringify(value) }] });
const failure = error => ({ ...result({ isSuccess: false, error }), isError: true });

export function validateEndpoint(configured) {
	let url;
	try { url = new URL(configured); } catch { throw new Error("Retravion endpoint is not configured. Rerun mcp_setup.ps1."); }
	if (url.protocol !== "https:" || !loopback.includes(url.hostname) || url.pathname !== "/mcp" ||
		url.username || url.password || url.search || url.hash)
		throw new Error("Retravion endpoint must be https://localhost:<port>/mcp. Rerun mcp_setup.ps1.");
	return url;
}

/** HTTPS limited to the certificate saved by setup. Verification is never disabled. */
export function createRequest(ca) {
	if (!ca) throw new Error("Retravion certificate is missing. Rerun mcp_setup.ps1.");
	return (url, { method = "POST", headers = {}, body, signal } = {}) => new Promise((resolve, reject) => {
		const request = https.request(url, { method, headers, ca, signal, timeout: 60000 }, response => {
			const chunks = [];
			let bytes = 0;
			response.on("data", chunk => {
				bytes += chunk.length;
				if (bytes > maxBytes) { request.destroy(new Error("Retravion response is too large.")); return; }
				chunks.push(chunk);
			});
			response.on("end", () => resolve({ status: response.statusCode, headers: response.headers, text: Buffer.concat(chunks).toString("utf8") }));
			response.on("error", reject);
		});
		request.on("timeout", () => request.destroy(new Error("Retravion did not respond.")));
		request.on("error", reject);
		request.end(body);
	});
}

/** Opens only an already validated loopback HTTPS URL, without a shell. */
export function openBrowser(url) {
	const [command, args] = process.platform === "win32" ? ["rundll32.exe", ["url.dll,FileProtocolHandler", url]]
		: process.platform === "darwin" ? ["open", [url]] : ["xdg-open", [url]];
	return new Promise(resolve => {
		try { execFile(command, args, { windowsHide: true }, error => resolve(!error)); }
		catch { resolve(false); }
	});
}

class HttpError extends Error {
	constructor(status) { super(`Retravion request failed with HTTP ${status}.`); this.status = status; }
}

export class RetravionTerminal {
	#endpoint; #request; #now; #sleep; #open; #tools;
	#pending; #token; #sessionId; #nextId = 1; #signal;

	constructor({ endpoint, request, tools = [], now = Date.now, sleep = delay, open = openBrowser }) {
		this.#endpoint = endpoint; this.#request = request; this.#tools = tools;
		this.#now = now; this.#sleep = sleep; this.#open = open;
	}

	get tools() { return [...connectionTools, ...this.#tools]; }

	async call(name, args = {}, signal) {
		this.#signal = signal;
		try {
			if (name === "rtv_connect") return await this.#connect();
			if (name === "rtv_complete_connection") return await this.#complete();
			if (name === "rtv_disconnect") return await this.#disconnect();
			if (!this.#tools.some(tool => tool.name === name)) return failure("Unknown Retravion tool.");
			if (!this.#token) return failure("Not connected. Ask the user, call rtv_connect, then rtv_complete_connection after they approve in the browser.");
			return await this.#rpc("tools/call", { name, arguments: args });
		} catch (error) {
			if (name === "rtv_connect") { this.dispose(); return failure("Retravion connection could not start. Check that Retravion is running, MCP is enabled by an administrator, and mcp_setup.ps1 was run for the current certificate."); }
			if (name === "rtv_complete_connection") { this.dispose(); return failure("Retravion connection could not be completed. Call rtv_connect again."); }
			if (error instanceof HttpError && [401, 403, 404].includes(error.status)) { this.dispose(); return failure(ended); }
			return failure("Retravion did not answer. Do not retry a write automatically: its outcome is unknown. Read the current state first and let the user decide.");
		} finally { this.#signal = undefined; }
	}

	async #post(path, token) {
		const url = new URL(path, this.#endpoint);
		const response = await this.#request(url, { method: "POST", signal: this.#signal,
			headers: { Accept: "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) } });
		if (response.status < 200 || response.status > 299) throw new HttpError(response.status);
		return response.text ? JSON.parse(response.text) : {};
	}

	async #connect() {
		this.dispose();
		const endpoint = validateEndpoint(this.#endpoint);
		const started = await this.#post("/mcp-connect/start");
		const target = new URL(started.verificationUri);
		const expires = Number(started.expiresIn);
		if (!/^[0-9A-F]{64}$/.test(started.deviceCode ?? "") || !/^[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(started.userCode ?? "") ||
			!(expires > 0) || target.origin !== endpoint.origin || target.pathname !== "/settings/ai-workflow" ||
			target.username || target.password)
			throw new Error("Invalid connection response.");
		this.#pending = { deviceCode: started.deviceCode, deadline: this.#now() + Math.min(600, expires) * 1000,
			interval: Math.max(1, Math.min(30, Number(started.interval) || 2)) * 1000 };
		const browserOpened = await this.#open(target.href);
		// The device code is the future credential: it never enters a tool result.
		return result({ isSuccess: true, browserOpened, verificationUri: target.href, userCode: started.userCode,
			message: (browserOpened ? "The browser was opened at Retravion." : "The browser could not be opened: show the user this link.") +
				" The user signs in if needed, checks the code and approves. The code can also be entered on the AI Workflow page. When they confirm, call rtv_complete_connection." });
	}

	async #complete() {
		const pending = this.#pending;
		if (!pending) return failure("No connection is waiting. Call rtv_connect first.");
		const wait = this.#now() + 60000;
		while (this.#now() < pending.deadline) {
			const { status } = await this.#post("/mcp-connect/status", pending.deviceCode);
			if (status === "approved") {
				this.#pending = undefined;
				this.#token = pending.deviceCode;
				await this.#rpc("initialize", { protocolVersion, capabilities: {}, clientInfo: { name: "Retravion terminal bridge", version } });
				await this.#send({ jsonrpc: "2.0", method: "notifications/initialized" });
				return result({ isSuccess: true, message: "Connected to Retravion. Access renews while in use and ends on browser logout or 30 idle minutes." });
			}
			if (status !== "pending") { this.dispose(); return failure(status === "denied" ? "The user denied the connection." : "The connection request expired. Call rtv_connect again."); }
			if (this.#now() >= wait) return failure("Still waiting for approval in the browser. Ask the user to approve, then call rtv_complete_connection again.");
			await this.#sleep(Math.min(pending.interval, Math.max(0, pending.deadline - this.#now())), undefined, { signal: this.#signal });
		}
		this.dispose();
		return failure("The connection request expired. Call rtv_connect again.");
	}

	async #disconnect() {
		const token = this.#token;
		this.dispose();
		try { if (token) await this.#post("/mcp-connect/disconnect", token); }
		catch { return result({ isSuccess: true, message: "Local credential discarded. Retravion could not be reached to end access; it ends on logout or after 30 idle minutes." }); }
		return result({ isSuccess: true, message: "Disconnected from Retravion." });
	}

	async #send(message) {
		const headers = { Authorization: `Bearer ${this.#token}`, Accept: "application/json, text/event-stream",
			"Content-Type": "application/json", "MCP-Protocol-Version": protocolVersion };
		if (this.#sessionId) headers["Mcp-Session-Id"] = this.#sessionId;
		const response = await this.#request(new URL(this.#endpoint), { method: "POST", headers, body: JSON.stringify(message), signal: this.#signal });
		if (response.headers["mcp-session-id"]) this.#sessionId = response.headers["mcp-session-id"];
		if (response.status < 200 || response.status > 299) throw new HttpError(response.status);
		return response;
	}

	async #rpc(method, params) {
		const id = this.#nextId++;
		const response = await this.#send({ jsonrpc: "2.0", id, method, params });
		const payloads = String(response.headers["content-type"] ?? "").includes("text/event-stream")
			? response.text.split(/\r?\n\r?\n/).map(event => event.split(/\r?\n/).filter(line => line.startsWith("data:")).map(line => line.slice(5).trimStart()).join("\n")).filter(Boolean)
			: [response.text];
		const message = payloads.map(text => JSON.parse(text)).find(candidate => candidate.id === id);
		if (!message) throw new Error("Retravion returned no response for this request.");
		if (message.error) return failure(message.error.message || "Retravion refused the request.");
		return message.result;
	}

	dispose() { this.#pending = undefined; this.#token = undefined; this.#sessionId = undefined; }
}

/** JSON-lines stdio MCP transport. */
export async function serve(terminal, input = process.stdin, output = process.stdout) {
	let buffer = "", queued = Promise.resolve(), initialized = false;
	const pending = new Map();
	const lifetime = new AbortController();
	const send = message => { if (!output.destroyed && !lifetime.signal.aborted) output.write(JSON.stringify({ jsonrpc: "2.0", ...message }) + "\n"); };
	const replyError = (id, code, message) => send({ id, error: { code, message } });
	const processLine = line => {
		let message;
		try { message = JSON.parse(line); } catch { replyError(null, -32700, "Invalid JSON."); return; }
		if (!message || message.jsonrpc !== "2.0" || typeof message.method !== "string") { replyError(null, -32600, "Invalid JSON-RPC request."); return; }
		if (message.method === "notifications/cancelled") { pending.get(message.params?.requestId)?.abort(); return; }
		if (message.id === undefined) return;
		const { id, method, params } = message;
		if (method === "initialize") {
			initialized = true;
			send({ id, result: { protocolVersion, capabilities: { tools: {} }, serverInfo: { name: "retravion-terminal", version },
				instructions: "Retravion Draft authoring. Load the retravion skill. Ask the user before rtv_connect; never ask for passwords or tokens." } });
			return;
		}
		if (!initialized) { replyError(id, -32000, "Initialize first."); return; }
		if (method === "ping") { send({ id, result: {} }); return; }
		if (method === "tools/list") { send({ id, result: { tools: terminal.tools } }); return; }
		if (method !== "tools/call") { replyError(id, -32601, "Method not found."); return; }
		const controller = new AbortController();
		pending.set(id, controller);
		// One request at a time: writes are never interleaved or replayed.
		queued = queued.then(async () => {
			try { send({ id, result: await terminal.call(params?.name, params?.arguments ?? {}, AbortSignal.any([lifetime.signal, controller.signal])) }); }
			catch { replyError(id, -32603, "Retravion response unavailable. Do not replay writes; read the current state first."); }
			finally { pending.delete(id); }
		});
	};
	input.setEncoding("utf8");
	input.on("data", chunk => {
		buffer += chunk;
		if (Buffer.byteLength(buffer, "utf8") > maxBytes) { lifetime.abort(); terminal.dispose(); input.destroy(); return; }
		let newline;
		while ((newline = buffer.indexOf("\n")) >= 0) {
			const line = buffer.slice(0, newline).trim(); buffer = buffer.slice(newline + 1);
			if (line) processLine(line);
		}
	});
	output.on("error", () => { lifetime.abort(); terminal.dispose(); input.destroy(); });
	await new Promise(resolve => { input.once("end", resolve); input.once("close", resolve); input.once("error", resolve); });
	lifetime.abort(); terminal.dispose();
	await queued;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
	// Setup writes these next to the bridge folder; nothing is read from the environment.
	const read = name => { try { return readFileSync(fileURLToPath(new URL(`../${name}`, import.meta.url)), "utf8"); } catch { return undefined; } };
	let endpoint, request, tools = [];
	try {
		endpoint = JSON.parse(read("endpoint.json") ?? "{}").endpoint;
		validateEndpoint(endpoint);
		request = createRequest(read("retravion.pem"));
		tools = JSON.parse(readFileSync(fileURLToPath(new URL("./tools.json", import.meta.url)), "utf8"));
	} catch (error) {
		// Fail closed, but stay reachable so the terminal can show why.
		request = () => Promise.reject(error);
	}
	await serve(new RetravionTerminal({ endpoint, request, tools }));
}
