export declare const MAX_RESPONSE_BYTES: number;
export declare const MAX_PAGE_SIZE: number;
export declare const maximumResponseBytes: number;
export declare const pageLimit: number;
export declare function parseOperationalPage(value: unknown, kind?: string, expectedTargetId?: string): Readonly<Record<string, unknown>>;
export declare function readBoundedJson(response: Response, kind?: string, expectedTargetId?: string, signal?: AbortSignal): Promise<Readonly<Record<string, unknown>>>;
export declare const parseOperationsPage: typeof parseOperationalPage;
export declare const validateOperationalPage: typeof parseOperationalPage;
export declare const readBoundedResponse: typeof readBoundedJson;
export declare const parsePage: typeof parseOperationalPage;
export declare function readBoundedBody(response: Response, signal?: AbortSignal, maximumBytes?: number): Promise<string>;
export declare class OperationalRequestError extends Error {}
export declare function safeStatusMessage(status: number): string;
