import { __resetMediaForTests, currentMediaToken, fillPagesTemplate, getMediaToken, pageUrl, reportMediaFailure, setMediaUser, thumbUrl } from "./booksMedia";

function mockMint(responses: (() => { status: number; body: unknown })[]) {
  const calls: string[] = [];
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    calls.push(url);
    const next = (responses.shift() ?? responses[responses.length - 1])();
    return { ok: next.status < 300, status: next.status, json: async () => next.body, headers: { get: () => null } };
  }));
  return calls;
}
const inHours = (h: number) => new Date(Date.now() + h * 3600_000).toISOString();

beforeEach(() => { __resetMediaForTests(); window.sessionStorage.clear(); });
afterEach(() => vi.unstubAllGlobals());

describe("Books/booksMedia — one session token, null-safe builders", () => {
  it("mints once, caches in the session, and builds the media-plane URLs", async () => {
    const calls = mockMint([() => ({ status: 200, body: { configured: true, token: "tok1", baseUrl: "https://host.example/", expiresUtc: inHours(12) } })]);
    setMediaUser("reader");
    expect(thumbUrl(5)).toBeNull();
    const t = await getMediaToken();
    expect(t?.token).toBe("tok1");
    expect(thumbUrl(5)).toBe("https://host.example/m/tok1/thumbs/5.webp");
    expect(pageUrl(5, 3, 1440.4)).toBe("https://host.example/m/tok1/pages/5/3?maxWidth=1440");
    await getMediaToken();
    expect(calls).toHaveLength(1);
    __resetMediaForTests();
    window.sessionStorage.setItem("books.mediaToken.v1", JSON.stringify({ token: "tok1", baseUrl: "https://host.example", expiresUtc: inHours(12), username: "reader" }));
    setMediaUser("reader");
    expect(currentMediaToken()?.token).toBe("tok1"); // a reload finds the session copy
  });

  it("an unconfigured plane yields null builders and backs off; a near-expiry token re-mints", async () => {
    const calls = mockMint([() => ({ status: 200, body: { configured: false } })]);
    expect(await getMediaToken()).toBeNull();
    expect(await getMediaToken()).toBeNull();
    expect(calls).toHaveLength(1);
    expect(thumbUrl(1)).toBeNull();
    __resetMediaForTests();
    window.sessionStorage.setItem("books.mediaToken.v1", JSON.stringify({ token: "old", baseUrl: "https://h", expiresUtc: inHours(0.1), username: "" }));
    expect(currentMediaToken()).toBeNull();
  });

  it("a failed image only re-mints when its token is stale; the template keeps the host's own token", async () => {
    const calls = mockMint([() => ({ status: 200, body: { configured: true, token: "tok2", baseUrl: "https://h", expiresUtc: inHours(12) } })]);
    await getMediaToken();
    reportMediaFailure("https://h/m/tok2/thumbs/9.webp");
    expect(calls).toHaveLength(1);
    reportMediaFailure("https://h/m/older/thumbs/9.webp");
    await Promise.resolve();
    expect(calls.length).toBeGreaterThanOrEqual(2);
    expect(fillPagesTemplate("https://h/m/tokX/pages/9/{page}", 4, 800)).toBe("https://h/m/tokX/pages/9/4?maxWidth=800");
    expect(fillPagesTemplate(null, 1)).toBeNull();
  });

  it("a different user invalidates the cached token", async () => {
    mockMint([() => ({ status: 200, body: { configured: true, token: "a", baseUrl: "https://h", expiresUtc: inHours(12) } })]);
    setMediaUser("one");
    await getMediaToken();
    expect(currentMediaToken()?.token).toBe("a");
    setMediaUser("two");
    expect(currentMediaToken()).toBeNull();
  });
});
