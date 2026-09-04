import { CHUNK_PLAIN_BYTES } from "./protocol.js";

export const MAX_CREATE_REQUEST_BYTES = 16 * 1024;
export const MAX_ITEMS = 100;
export const MAX_BYTES = 2 * 1024 * 1024 * 1024;
export const MAX_OBJECT_BYTES = 6 * 1024 * 1024;
export const MAX_MANIFEST_BYTES = 512 * 1024;
export const MAX_TTL_SECONDS = 7 * 24 * 60 * 60;
export const MIN_TTL_SECONDS = 60;
export const RESERVATION_TTL_MS = 10 * 60 * 1000;
export const MAX_CHUNK_INDEX = Math.ceil(MAX_BYTES / CHUNK_PLAIN_BYTES) - 1;
export const MAX_COORDINATOR_OBJECTS = MAX_ITEMS + Math.ceil(MAX_BYTES / CHUNK_PLAIN_BYTES) + 1;
