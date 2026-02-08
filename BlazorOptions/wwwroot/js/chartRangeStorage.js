function normalizeRange(range) {
    if (!range) {
        return null;
    }

    const xMin = Number.isFinite(range.XMin) ? range.XMin : range.xMin;
    const xMax = Number.isFinite(range.XMax) ? range.XMax : range.xMax;
    const yMin = Number.isFinite(range.YMin) ? range.YMin : range.yMin;
    const yMax = Number.isFinite(range.YMax) ? range.YMax : range.yMax;
    if (!Number.isFinite(xMin) || !Number.isFinite(xMax) || !Number.isFinite(yMin) || !Number.isFinite(yMax)) {
        return null;
    }

    if (xMin >= xMax || yMin >= yMax) {
        return null;
    }

    return { XMin: xMin, XMax: xMax, YMin: yMin, YMax: yMax };
}

export function getRange(storageKey) {
    if (!storageKey) {
        return null;
    }

    try {
        const raw = localStorage.getItem(storageKey);
        if (!raw) {
            return null;
        }

        return normalizeRange(JSON.parse(raw));
    } catch {
        return null;
    }
}

export function setRange(storageKey, range) {
    if (!storageKey) {
        return;
    }

    const normalized = normalizeRange(range);
    try {
        if (!normalized) {
            localStorage.removeItem(storageKey);
            return;
        }

        localStorage.setItem(storageKey, JSON.stringify(normalized));
    } catch {
        // ignore storage failures (private mode, quota)
    }
}

export function getTimeIntervalMs(storageKey) {
    if (!storageKey) {
        return null;
    }

    try {
        const raw = localStorage.getItem(storageKey);
        if (!raw) {
            return null;
        }

        const value = Number(raw);
        if (!Number.isFinite(value) || value <= 0) {
            return null;
        }

        return value;
    } catch {
        return null;
    }
}

export function setTimeIntervalMs(storageKey, intervalMs) {
    if (!storageKey) {
        return;
    }

    try {
        const value = Number(intervalMs);
        if (!Number.isFinite(value) || value <= 0) {
            localStorage.removeItem(storageKey);
            return;
        }

        localStorage.setItem(storageKey, String(value));
    } catch {
        // ignore storage failures (private mode, quota)
    }
}
