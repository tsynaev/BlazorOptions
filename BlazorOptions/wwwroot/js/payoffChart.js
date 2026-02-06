const instances = new Map();
let nextId = 1;

export function init(element, dotNetRef) {
    const chart = echarts.init(element, null, { renderer: 'canvas', useDirtyRect: true });
    const instanceId = `payoff_${nextId++}`;

    const resizeObserver = new ResizeObserver(() => chart.resize());
    resizeObserver.observe(element);

    const zr = chart.getZr();
    const clickHandler = (evt) => {
        const point = getPoint(evt);
        selectPriceAtPoint(instance, point);
    };
    zr.on('click', clickHandler);
    chart.on('click', (params) => {
        if (instance.isDragging || performance.now() < instance.suppressClickUntil) {
            return;
        }
        if (!params || !params.event) {
            return;
        }

        if (Array.isArray(params.value) && Number.isFinite(params.value[0])) {
            instance.dotNetRef.invokeMethodAsync('OnChartClick', params.value[0]);
            return;
        }

        const point = [params.event.zrX, params.event.zrY];
        selectPriceAtPoint(instance, point);
    });

    const instance = {
        chart,
        dotNetRef,
        resizeObserver,
        touchHandler: null,
        clickHandler,
        rangeTimer: null,
        axisDrag: null,
        rangeOverride: null,
        isDragging: false,
        clickState: null,
        pointerClick: null,
        pointerDownHandler: null,
        pointerMoveHandler: null,
        pointerUpHandler: null,
        domClickHandler: null,
        markers: [],
        selectedPrice: null,
        suppressClickUntil: 0,
        lastOption: null,
        seriesCache: new Map(),
        legendSelected: null,
        pointerDragActive: false,
        tooltipHidden: false
    };
    instance.touchHandler = (evt) => {
        if (!evt.cancelable) {
            return;
        }

        const touch = evt.touches && evt.touches.length > 0 ? evt.touches[0] : null;
        if (!touch) {
            return;
        }

        const rect = instance.chart.getDom().getBoundingClientRect();
        const point = [touch.clientX - rect.left, touch.clientY - rect.top];
        const mode = getAxisDragMode(instance.chart, point);
        if (mode === 'plot' || mode === 'x' || mode === 'y') {
            evt.preventDefault();
        }
    };
    instance.pointerDownHandler = (evt) => handlePointerDown(instance, evt);
    instance.pointerMoveHandler = (evt) => handlePointerMove(instance, evt);
    instance.pointerUpHandler = (evt) => handlePointerUp(instance, evt);
    instance.domClickHandler = (evt) => handleDomClick(instance, evt);
    element.addEventListener('pointerdown', instance.pointerDownHandler);
    element.addEventListener('pointermove', instance.pointerMoveHandler);
    element.addEventListener('pointerup', instance.pointerUpHandler);
    element.addEventListener('click', instance.domClickHandler);
    element.addEventListener('touchstart', instance.touchHandler, { passive: false });
    element.addEventListener('touchmove', instance.touchHandler, { passive: false });

    chart.on('dataZoom', () => scheduleRangeChanged(instance));
    chart.on('legendselectchanged', (params) => handleLegendToggle(instance, params));
    zr.on('mousedown', (evt) => handleAxisDragStart(instance, evt));
    zr.on('mousemove', (evt) => handleAxisDragMove(instance, evt));
    zr.on('mouseup', () => handleAxisDragEnd(instance));
    zr.on('globalout', () => handleAxisDragEnd(instance));
    zr.on('touchstart', (evt) => handleAxisDragStart(instance, evt));
    zr.on('touchmove', (evt) => handleAxisDragMove(instance, evt));
    zr.on('touchend', () => handleAxisDragEnd(instance));
    instances.set(instanceId, instance);

    return instanceId;
}

export function setOption(instanceId, option) {
    const instance = instances.get(instanceId);
    if (!instance) {
        return;
    }

    const normalized = normalizeOption(option, instance);
    instance.lastOption = normalized;
    instance.chart.setOption(normalized, { notMerge: true, lazyUpdate: false });
    cacheSeries(instance, normalized);
    if (instance.legendSelected) {
        applyLegendSelection(instance);
    }
}

export function setSelectedPrice(instanceId, priceOrNull) {
    const instance = instances.get(instanceId);
    if (!instance) {
        return;
    }

    instance.selectedPrice = priceOrNull;
    applyMarkers(instance);
}

export function setMarkers(instanceId, markers) {
    const instance = instances.get(instanceId);
    if (!instance) {
        return;
    }

    instance.markers = Array.isArray(markers) ? markers : [];
    applyMarkers(instance);
}

function formatPrice(value) {
    if (!Number.isFinite(value)) {
        return '';
    }

    const abs = Math.abs(value);
    if (abs === 0) {
        return '0';
    }

    if (abs >= 100) {
        return Math.round(value).toString();
    }

    if (abs >= 1) {
        const rounded = Math.round(value);
        const hasFraction = Math.abs(value - rounded) > 1e-9;
        if (hasFraction) {
            const oneDecimal = value.toFixed(1);
            return oneDecimal;
        }
        return rounded.toString();
    }

    const fixed = abs.toFixed(12).replace(/0+$/, '');
    const parts = fixed.split('.');
    const decimals = parts[1] ?? '';
    const nonZeroPositions = [];
    for (let i = 0; i < decimals.length; i++) {
        if (decimals[i] !== '0') {
            nonZeroPositions.push(i);
            if (nonZeroPositions.length >= 3) {
                break;
            }
        }
    }
    if (nonZeroPositions.length === 0) {
        return '0';
    }

    const end = Math.min(decimals.length, nonZeroPositions[nonZeroPositions.length - 1] + 1);
    const slice = decimals.slice(0, end);
    return `${value < 0 ? '-' : ''}0.${slice}`;
}

function applyMarkers(instance) {
    const markLineData = [];
    const selected = instance.selectedPrice;

    if (selected != null) {
        markLineData.push({
            xAxis: selected,
            lineStyle: { color: '#111827', width: 1, type: 'solid' },
            label: {
                show: true,
                formatter: `Futures ${formatPrice(selected)}`,
                rotate: 90,
                position: 'insideEndTop',
                color: '#111827',
                fontSize: 9,
                distance: 0,
                offset: [0, 0]
            }
        });
    }

    for (const marker of instance.markers) {
        if (!marker) {
            continue;
        }

        const price = Number.isFinite(marker.price) ? marker.price : marker.Price;
        if (!Number.isFinite(price)) {
            continue;
        }

        const text = marker.text ?? marker.Text ?? '';
        const color = marker.color ?? marker.Color ?? '#111827';
        markLineData.push({
            xAxis: price,
            lineStyle: { color, width: 1, type: 'dashed' },
            label: {
                show: Boolean(text),
                formatter: text,
                rotate: 90,
                position: 'insideEndTop',
                color,
                fontSize: 9,
                distance: 0,
                offset: [0, 0]
            }
        });
    }

    instance.chart.setOption({
        series: [{
            id: '__selected__',
            markLine: {
                symbol: 'none',
                data: markLineData
            }
        }]
    });

    if (selected != null) {
        instance.chart.dispatchAction({
            type: 'updateAxisPointer',
            xAxisIndex: 0,
            value: selected
        });
    }
}


export function dispose(instanceId) {
    const instance = instances.get(instanceId);
    if (!instance) {
        return;
    }

    instance.resizeObserver.disconnect();
    const element = instance.chart.getDom();
    element.removeEventListener('touchstart', instance.touchHandler);
    element.removeEventListener('touchmove', instance.touchHandler);
    element.removeEventListener('pointerdown', instance.pointerDownHandler);
    element.removeEventListener('pointermove', instance.pointerMoveHandler);
    element.removeEventListener('pointerup', instance.pointerUpHandler);
    element.removeEventListener('click', instance.domClickHandler);
    const zr = instance.chart.getZr();
    zr.off('click', instance.clickHandler);
    instance.chart.off('click');
    zr.off('mousedown');
    zr.off('mousemove');
    zr.off('mouseup');
    zr.off('globalout');
    zr.off('touchstart');
    zr.off('touchmove');
    zr.off('touchend');
    instance.chart.dispose();
    instances.delete(instanceId);
}

function handleDomClick(instance, evt) {
    if (instance.isDragging || performance.now() < instance.suppressClickUntil) {
        return;
    }
    const rect = instance.chart.getDom().getBoundingClientRect();
    const point = [evt.clientX - rect.left, evt.clientY - rect.top];
    selectPriceAtPoint(instance, point);
}

function scheduleRangeChanged(instance) {
    if (instance.rangeTimer) {
        clearTimeout(instance.rangeTimer);
    }

    // Debounce to avoid flooding .NET while the user is still panning/zooming.
    instance.rangeTimer = setTimeout(() => {
        const xRange = instance.rangeOverride?.x ?? getVisibleRange(instance.chart, 'x', 0);
        const yRange = instance.rangeOverride?.y ?? getVisibleRange(instance.chart, 'y', 0);
        if (!xRange || !yRange) {
            return;
        }

        instance.dotNetRef.invokeMethodAsync('OnRangeChanged', xRange.min, xRange.max, yRange.min, yRange.max);
        instance.rangeOverride = null;
    }, 300);
}

function getVisibleRange(chart, axis, axisIndex) {
    const axisComponent = chart.getModel().getComponent(`${axis}Axis`, axisIndex);
    if (!axisComponent) {
        return null;
    }

    const extent = axisComponent.axis.scale.getExtent();
    const dataZoom = chart.getOption().dataZoom ?? [];
    const zoom = dataZoom.find((item) => {
        const idx = axis === 'x' ? item.xAxisIndex : item.yAxisIndex;
        return idx === axisIndex || (Array.isArray(idx) && idx.includes(axisIndex));
    });

    if (!zoom) {
        return { min: extent[0], max: extent[1] };
    }

    // Use explicit startValue/endValue when available to reflect the actual visible window.
    if (Number.isFinite(zoom.startValue) && Number.isFinite(zoom.endValue)) {
        return { min: zoom.startValue, max: zoom.endValue };
    }

    const start = Number.isFinite(zoom.start) ? zoom.start : 0;
    const end = Number.isFinite(zoom.end) ? zoom.end : 100;
    const span = extent[1] - extent[0];
    const min = extent[0] + (span * start) / 100;
    const max = extent[0] + (span * end) / 100;
    return { min, max };
}

function normalizeOption(option, instance) {
    const normalized = option ?? {};

    if (normalized.xAxis?.axisLabel) {
        normalized.xAxis.axisLabel.formatter = (value) => String(value);
    }

    if (normalized.yAxis?.axisLabel) {
        normalized.yAxis.axisLabel.formatter = (value) => Number(value).toFixed(2);
    }

    normalized.tooltip = normalized.tooltip ?? {};
    normalized.tooltip.formatter = (params) => {
        if (!Array.isArray(params) || params.length === 0) {
            return '';
        }

        const axisValue = params[0].axisValue;
        const lines = [`Price: ${formatPrice(axisValue)}`];

        for (const entry of params) {
            const value = Array.isArray(entry.data) ? entry.data[1] : entry.value;
            const pnl = Number(value).toFixed(2);
            const seriesOption = instance?.lastOption?.series?.[entry.seriesIndex];
            const kind = seriesOption?.payoffKind ? ` ${seriesOption.payoffKind}` : '';
            lines.push(`${entry.marker}${entry.seriesName}${kind}: ${pnl}`);
        }

        return lines.join('<br/>');
    };

    if (Array.isArray(normalized.series)) {
        for (const series of normalized.series) {
            if (!series) {
                continue;
            }

            const id = typeof series.id === 'string' ? series.id : '';
            if (id.endsWith('-be-exp') || id.endsWith('-be-temp')) {
                series.label = series.label ?? {};
                series.label.formatter = (params) => formatPrice(Array.isArray(params.value) ? params.value[0] : params.value);
            }
        }
    }

    return normalized;
}

function cacheSeries(instance, option) {
    instance.seriesCache = instance.seriesCache ?? new Map();
    instance.seriesCache.clear();
    if (!option || !Array.isArray(option.series)) {
        return;
    }

    for (const series of option.series) {
        const name = series?.strategyName ?? series?.name;
        if (!series || !series.id || !name || !Array.isArray(series.data)) {
            continue;
        }

        instance.seriesCache.set(series.id, {
            strategyName: name,
            data: series.data
        });
    }
}

function handleLegendToggle(instance, params) {
    if (!params || !params.selected) {
        return;
    }

    instance.legendSelected = params.selected;
    applyLegendSelection(instance);
}

function applyLegendSelection(instance) {
    if (!instance.legendSelected || !instance.seriesCache || instance.seriesCache.size === 0) {
        return;
    }

    const updates = [];
    for (const [id, entry] of instance.seriesCache.entries()) {
        const isVisible = instance.legendSelected[entry.strategyName] !== false;
        updates.push({
            id,
            data: isVisible ? entry.data : []
        });
    }

    instance.chart.setOption({ series: updates }, { notMerge: false, lazyUpdate: true });
}

function handleAxisDragStart(instance, evt) {
    const point = getPoint(evt);
    const mode = getAxisDragMode(instance.chart, point);
    if (!mode) {
        return;
    }

    if (!instance.tooltipHidden) {
        instance.tooltipHidden = true;
        instance.chart.setOption({ tooltip: { show: false } }, { notMerge: false, lazyUpdate: true });
    }

    instance.isDragging = false;
    instance.clickState = { start: point, moved: false, mode };
    const anchor = getAxisValueAtPoint(instance.chart, mode, point);
    instance.axisDrag = {
        mode,
        startX: point[0],
        startY: point[1],
        anchor,
        startValueAtPoint: anchor,
        startRange: {
            x: getVisibleRange(instance.chart, 'x', 0),
            y: getVisibleRange(instance.chart, 'y', 0)
        },
        rect: getGridRect(instance.chart)
    };
}

function handleAxisDragMove(instance, evt) {
    const point = getPoint(evt);
    const mode = getAxisDragMode(instance.chart, point);
    instance.chart.getZr().setCursorStyle(
        mode === 'x'
            ? 'ew-resize'
            : mode === 'y'
                ? 'ns-resize'
                : mode === 'plot'
                    ? 'move'
                    : 'default'
    );

    if (!instance.axisDrag) {
        return;
    }

    const drag = instance.axisDrag;
    if (!drag.rect || !drag.startRange.x || !drag.startRange.y) {
        return;
    }

    if (drag.mode === 'x') {
        const deltaPixels = point[0] - drag.startX;
        const deltaValue = pixelsToValueDelta(drag.startRange.x, deltaPixels, drag.rect.width);
        // Dragging right/left on the axis expands/contracts by moving the min while keeping max fixed.
        const zoomed = stretchRangeFromMax(drag.startRange.x, deltaValue);
        applyZoom(instance.chart, 'x', 0, zoomed);
        instance.rangeOverride = { x: zoomed };
    } else if (drag.mode === 'y') {
        const deltaPixels = drag.startY - point[1];
        // Dragging up shrinks the range (max down, min up). Dragging down expands it.
        const zoomed = scaleRange(drag.startRange.y, -deltaPixels, drag.rect.height, 0.9);
        instance.chart.setOption({ yAxis: { min: zoomed.min, max: zoomed.max } }, { notMerge: false, lazyUpdate: true });
        instance.rangeOverride = { y: zoomed };
    } else if (drag.mode === 'plot') {
        const deltaPixelsX = point[0] - drag.startX;
        const deltaPixelsY = drag.startY - point[1];
        const deltaX = -pixelsToValueDelta(drag.startRange.x, deltaPixelsX, drag.rect.width);
        const deltaY = -pixelsToValueDelta(drag.startRange.y, deltaPixelsY, drag.rect.height);
        const shiftedX = shiftRange(drag.startRange.x, deltaX);
        const shiftedY = shiftRange(drag.startRange.y, deltaY);
        applyZoom(instance.chart, 'x', 0, shiftedX);
        instance.chart.setOption({ yAxis: { min: shiftedY.min, max: shiftedY.max } }, { notMerge: false, lazyUpdate: true });
        instance.rangeOverride = { x: shiftedX, y: shiftedY };
    }

    if (instance.clickState && !instance.clickState.moved) {
        const dx = point[0] - instance.clickState.start[0];
        const dy = point[1] - instance.clickState.start[1];
        if (Math.hypot(dx, dy) > 4) {
            instance.clickState.moved = true;
            instance.isDragging = true;
        }
    }
}

function handleAxisDragEnd(instance) {
    if (instance.axisDrag && instance.rangeOverride) {
        scheduleRangeChanged(instance);
    }
    if (instance.clickState?.moved || instance.isDragging) {
        instance.suppressClickUntil = performance.now() + 250;
    }
    instance.axisDrag = null;
    instance.clickState = null;
    instance.isDragging = false;
    instance.chart.getZr().setCursorStyle('default');

    if (instance.tooltipHidden) {
        instance.tooltipHidden = false;
        instance.chart.setOption({ tooltip: { show: true } }, { notMerge: false, lazyUpdate: true });
    }
}

function getPoint(evt) {
    const x = Number.isFinite(evt.offsetX) ? evt.offsetX : evt.zrX;
    const y = Number.isFinite(evt.offsetY) ? evt.offsetY : evt.zrY;
    return [x, y];
}

function getAxisDragMode(chart, point) {
    const rect = getGridRect(chart);
    if (!rect) {
        return null;
    }

    const [x, y] = point;
    const axisBandX = { min: rect.x, max: rect.x + rect.width, top: rect.y + rect.height - 6, bottom: rect.y + rect.height + 36 };
    const axisBandY = { min: rect.y, max: rect.y + rect.height, left: rect.x - 44, right: rect.x + 6 };

    if (x >= axisBandX.min && x <= axisBandX.max && y >= axisBandX.top && y <= axisBandX.bottom) {
        return 'x';
    }

    if (y >= axisBandY.min && y <= axisBandY.max && x >= axisBandY.left && x <= axisBandY.right) {
        return 'y';
    }

    if (x >= rect.x && x <= rect.x + rect.width && y >= rect.y && y <= rect.y + rect.height) {
        return 'plot';
    }

    return null;
}

function getGridRect(chart) {
    const grid = chart.getModel().getComponent('grid', 0);
    return grid ? grid.coordinateSystem.getRect() : null;
}

function scaleRange(range, delta, size, strength) {
    const span = range.max - range.min;
    const factor = Math.min(5, Math.max(0.2, 1 + (delta / size) * strength));
    const newSpan = span * factor;
    const center = (range.min + range.max) / 2;
    return { min: center - newSpan / 2, max: center + newSpan / 2 };
}

function scaleRangeAroundAnchor(range, delta, size, strength, anchor) {
    if (!Number.isFinite(anchor)) {
        return scaleRange(range, delta, size, strength);
    }

    const factor = Math.min(5, Math.max(0.2, 1 + (delta / size) * strength));
    return {
        min: anchor + (range.min - anchor) * factor,
        max: anchor + (range.max - anchor) * factor
    };
}

function stretchRangeFromMin(range, delta) {
    const min = range.min;
    let max = range.max + delta;
    if (!Number.isFinite(min) || !Number.isFinite(max)) {
        return range;
    }

    if (max <= min + 1e-6) {
        max = min + 1e-6;
    }

    return { min, max };
}

function stretchRangeFromMax(range, delta) {
    const max = range.max;
    let min = range.min - delta;
    if (!Number.isFinite(min) || !Number.isFinite(max)) {
        return range;
    }

    if (max <= min + 1e-6) {
        min = max - 1e-6;
    }

    return { min, max };
}

function shiftRange(range, delta) {
    if (!range || !Number.isFinite(range.min) || !Number.isFinite(range.max)) {
        return range;
    }

    return { min: range.min + delta, max: range.max + delta };
}

function getAxisValueAtPoint(chart, mode, point) {
    if (mode === 'x') {
        const value = chart.convertFromPixel({ xAxisIndex: 0 }, point);
        return Array.isArray(value) ? value[0] : value;
    }

    const value = chart.convertFromPixel({ yAxisIndex: 0 }, point);
    return Array.isArray(value) ? value[0] : value;
}

function pixelsToValueDelta(range, deltaPixels, sizePixels) {
    if (!range || !Number.isFinite(range.min) || !Number.isFinite(range.max) || sizePixels <= 0) {
        return 0;
    }

    const span = range.max - range.min;
    return (deltaPixels / sizePixels) * span;
}

function applyZoom(chart, axis, axisIndex, range) {
    if (!Number.isFinite(range.min) || !Number.isFinite(range.max)) {
        return;
    }

    if (axis === 'x') {
        chart.dispatchAction({
            type: 'dataZoom',
            xAxisIndex: axisIndex,
            startValue: range.min,
            endValue: range.max
        });
    } else {
        chart.dispatchAction({
            type: 'dataZoom',
            yAxisIndex: axisIndex,
            startValue: range.min,
            endValue: range.max
        });
    }
}

function selectPriceAtPoint(instance, point) {
    if (!instance) {
        return;
    }
    if (instance.isDragging || performance.now() < instance.suppressClickUntil) {
        return;
    }

    const rect = getGridRect(instance.chart);
    if (!rect || point[0] < rect.x || point[0] > rect.x + rect.width || point[1] < rect.y || point[1] > rect.y + rect.height) {
        return;
    }

    // Map pixel position to x-axis value so clicks select the exact price.
    let axisValue = instance.chart.convertFromPixel({ xAxisIndex: 0 }, point);
    let xValue = Array.isArray(axisValue) ? axisValue[0] : axisValue;
    if (!Number.isFinite(xValue)) {
        axisValue = instance.chart.convertFromPixel({ seriesIndex: 0 }, point);
        xValue = Array.isArray(axisValue) ? axisValue[0] : axisValue;
    }
    if (Number.isFinite(xValue)) {
        instance.dotNetRef.invokeMethodAsync('OnChartClick', xValue);
    }
}

function handlePointerDown(instance, evt) {
    if (!instance || instance.isDragging) {
        return;
    }
    if (evt.pointerType === 'mouse') {
        return;
    }

    const rect = instance.chart.getDom().getBoundingClientRect();
    const point = [evt.clientX - rect.left, evt.clientY - rect.top];
    instance.pointerClick = {
        start: point,
        moved: false
    };
    instance.pointerDragActive = true;
    handleAxisDragStart(instance, { offsetX: point[0], offsetY: point[1] });
}

function handlePointerMove(instance, evt) {
    if (!instance || !instance.pointerClick) {
        return;
    }
    if (evt.pointerType === 'mouse') {
        return;
    }

    const rect = instance.chart.getDom().getBoundingClientRect();
    const x = evt.clientX - rect.left;
    const y = evt.clientY - rect.top;
    const dx = x - instance.pointerClick.start[0];
    const dy = y - instance.pointerClick.start[1];
    if (Math.hypot(dx, dy) > 4) {
        instance.pointerClick.moved = true;
    }

    if (instance.pointerDragActive) {
        handleAxisDragMove(instance, { offsetX: x, offsetY: y });
    }
}

function handlePointerUp(instance, evt) {
    if (!instance || !instance.pointerClick) {
        instance.pointerClick = null;
        return;
    }
    if (evt.pointerType === 'mouse') {
        instance.pointerClick = null;
        return;
    }

    if (instance.pointerDragActive) {
        handleAxisDragEnd(instance);
        instance.pointerDragActive = false;
    }

    if (instance.pointerClick.moved || performance.now() < instance.suppressClickUntil) {
        instance.pointerClick = null;
        return;
    }

    const rect = instance.chart.getDom().getBoundingClientRect();
    const point = [evt.clientX - rect.left, evt.clientY - rect.top];
    selectPriceAtPoint(instance, point);
    instance.pointerClick = null;
}
