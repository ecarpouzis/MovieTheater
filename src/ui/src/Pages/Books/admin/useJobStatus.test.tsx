import { act, renderHook, waitFor } from "@testing-library/react";
import useJobStatus from "./useJobStatus";
import type { JobStatus } from "./adminApi";

// A controllable EventSource double: the test pushes `status` frames.
class FakeEventSource {
  static instances: FakeEventSource[] = [];
  url: string;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  listeners = new Map<string, ((e: MessageEvent) => void)[]>();
  closed = false;
  constructor(url: string) { this.url = url; FakeEventSource.instances.push(this); }
  addEventListener(type: string, fn: (e: MessageEvent) => void) { this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]); }
  close() { this.closed = true; }
  emit(type: string, data: unknown) { for (const fn of this.listeners.get(type) ?? []) fn({ data: JSON.stringify(data) } as MessageEvent); }
}

const job = (over: Partial<JobStatus> = {}): JobStatus => ({ kind: "scan", state: "idle", processed: 0, remaining: 0, nextCursor: null, failed: 0, startedAt: null, finishedAt: null, error: null, lastLine: null, batches: 0, ...over });

let polled: JobStatus = job();
beforeEach(() => {
  FakeEventSource.instances = [];
  vi.stubGlobal("EventSource", FakeEventSource);
  polled = job();
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, status: 200, headers: { get: () => null }, json: async () => polled })));
});
afterEach(() => vi.unstubAllGlobals());

describe("Books/admin/useJobStatus — SSE with a poll behind it", () => {
  it("polls for the first snapshot, opens the feed once the job runs, follows its frames, and closes on done", async () => {
    const { result } = renderHook(() => useJobStatus("scan"));
    await waitFor(() => expect(result.current.status?.state).toBe("idle"));
    expect(FakeEventSource.instances).toHaveLength(0); // idle: no feed

    // A start answers with a running status; the card applies it and the feed opens.
    act(() => { result.current.apply(job({ state: "running", processed: 10, remaining: 90 })); });
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    const es = FakeEventSource.instances[0];
    expect(es.url).toBe("/API/Books/admin/scan/events");
    act(() => { es.onopen?.(); });
    expect(result.current.live).toBe(true);
    act(() => { es.emit("status", job({ state: "running", processed: 50, remaining: 50 })); });
    expect(result.current.status?.processed).toBe(50);
    act(() => { es.emit("status", job({ state: "done", processed: 100, remaining: 0 })); });
    expect(result.current.status?.state).toBe("done");
    expect(es.closed).toBe(true);
    expect(result.current.running).toBe(false);
  });

  it("a job kind with a colon reaches the host encoded", async () => {
    polled = job({ kind: "recompute:series", state: "running", processed: 1, remaining: 5 });
    renderHook(() => useJobStatus("recompute:series"));
    await waitFor(() => expect(FakeEventSource.instances).toHaveLength(1));
    expect(FakeEventSource.instances[0].url).toBe("/API/Books/admin/recompute%3Aseries/events");
  });
});
