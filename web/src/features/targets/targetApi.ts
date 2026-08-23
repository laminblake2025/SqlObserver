import type {
  ObservationTargetPage,
  ObservationTargetSummary,
  RegisterObservationTargetRequest,
} from "./targetTypes";

const targetsPath = "/api/v1/observation-targets";

export async function listObservationTargets(
  signal: AbortSignal,
): Promise<ObservationTargetPage> {
  const response = await fetch(`${targetsPath}?limit=50`, {
    credentials: "same-origin",
    headers: { Accept: "application/json" },
    signal,
  });

  return readJson<ObservationTargetPage>(response);
}

export async function registerObservationTarget(
  request: RegisterObservationTargetRequest,
  signal: AbortSignal,
): Promise<ObservationTargetSummary> {
  const response = await fetch(targetsPath, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    body: JSON.stringify(request),
    signal,
  });

  return readJson<ObservationTargetSummary>(response);
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    if (response.status === 429) {
      throw new Error("Too many administrative requests. Wait briefly and retry.");
    }

    throw new Error(`SqlObserver request failed with status ${response.status}.`);
  }

  return (await response.json()) as T;
}
