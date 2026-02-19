const instances = new Map();
let nextId = 1;
const candleMetaBySeries = new Map();

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
        tooltipHidden: false,
        currentRangeX: null,
        currentRangeY: null,
        currentRangeTime: null,
        preserveRange: false,
        pinnedRangeY: null
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

    // dataZoom disabled; we use custom drag handlers for ranges.
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
    syncAxisRanges(instance, normalized);
    const hasTimeAxis = Array.isArray(instance.lastOption?.yAxis) && instance.lastOption.yAxis.length > 1;
    if (!hasTimeAxis) {
        instance.currentRangeTime = null;
    }
    if (instance.lastOption?.xAxis && instance.currentRangeX) {
        instance.chart.setOption({ xAxis: { min: instance.currentRangeX.min, max: instance.currentRangeX.max } }, { notMerge: false, lazyUpdate: true });
    }
    if (hasTimeAxis) {
        if (instance.currentRangeY || instance.currentRangeTime) {
            instance.chart.setOption({
                yAxis: [
                    instance.currentRangeY ? { min: instance.currentRangeY.min, max: instance.currentRangeY.max } : {},
                    instance.currentRangeTime ? { min: instance.currentRangeTime.min, max: instance.currentRangeTime.max } : {}
                ]
            }, { notMerge: false, lazyUpdate: true });
        }
    } else if (instance.lastOption?.yAxis && instance.currentRangeY) {
        instance.chart.setOption({ yAxis: { min: instance.currentRangeY.min, max: instance.currentRangeY.max } }, { notMerge: false, lazyUpdate: true });
    }
    if (instance.legendSelected) {
        applyLegendSelection(instance);
    }
    refreshCandleMetaFromOption(instance, normalized);
    applyMarkers(instance);
}

export function setSelectedPrice(instanceId, priceOrNull) {
    const instance = instances.get(instanceId);
    if (!instance) {
        return;
    }

    instance.selectedPrice = priceOrNull;
    if (Number.isFinite(priceOrNull)) {
        // Skip auto-centering until payoff series exists; otherwise ECharts can report a tiny default span.
        if (!hasPayoffSeriesData(instance)) {
            applyMarkers(instance);
            return;
        }

        const range = instance.currentRangeX ?? getVisibleRange(instance.chart, 'x', 0);
        if (range && Number.isFinite(range.min) && Number.isFinite(range.max)) {
            const span = range.max - range.min;
            if (span > 0 && (priceOrNull < range.min || priceOrNull > range.max)) {
                const min = priceOrNull - span / 2;
                const max = priceOrNull + span / 2;
                instance.currentRangeX = { min, max };
                instance.chart.setOption({ xAxis: { min, max } }, { notMerge: false, lazyUpdate: true });
            }
        }
    }
    applyMarkers(instance);
}

function hasPayoffSeriesData(instance) {
    const series = instance?.lastOption?.series;
    if (!Array.isArray(series)) {
        return false;
    }

    for (const item of series) {
        if (!item || typeof item.id !== 'string') {
            continue;
        }

        if (!item.id.endsWith('-temp') && !item.id.endsWith('-expired')) {
            continue;
        }

        if (Array.isArray(item.data) && item.data.length > 1) {
            return true;
        }
    }

    return false;
}

export function setMarkers(instanceId, markers) {
    const instance = instances.get(instanceId);
    if (!instance) {
        return;
    }

    instance.markers = Array.isArray(markers) ? markers : [];
    applyMarkers(instance);
}


export function updateCandles(instanceId, candles) {
    const instance = instances.get(instanceId);
    if (!instance || !Array.isArray(candles)) {
        return;
    }

    let minTime = Number.POSITIVE_INFINITY;
    let maxTime = Number.NEGATIVE_INFINITY;
    const candleData = candles.map((c) => {
        const time = Number.isFinite(c.time) ? c.time : c.Time;
        const open = Number.isFinite(c.open) ? c.open : c.Open;
        const high = Number.isFinite(c.high) ? c.high : c.High;
        const low = Number.isFinite(c.low) ? c.low : c.Low;
        const close = Number.isFinite(c.close) ? c.close : c.Close;
        if (Number.isFinite(time)) {
            minTime = Math.min(minTime, time);
            maxTime = Math.max(maxTime, time);
        }
        return [time, open, close, low, high];
    });

    const lineData = candleData.map((c) => [c[2], c[0]]);

    const option = {
        series: [
            { id: '__ticker_candles__', data: candleData },
            { id: '__ticker_line__', data: lineData }
        ]
    };

    const hasTimeAxis = Array.isArray(instance.lastOption?.yAxis) && instance.lastOption.yAxis.length > 1;
    if (hasTimeAxis && !instance.preserveRange && Number.isFinite(minTime) && Number.isFinite(maxTime)) {
        instance.currentRangeTime = { min: minTime, max: maxTime };
        option.yAxis = [{}, { min: minTime, max: maxTime }];
    }

    instance.chart.setOption(option, { notMerge: false, lazyUpdate: true });
    refreshCandleMeta(instance, candleData);
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
    const isDark = Boolean(instance.lastOption?.backgroundColor) && instance.lastOption.backgroundColor !== '#ffffff';
    const selectedColor = isDark ? '#e2e8f0' : '#111827';

    if (selected != null) {
        markLineData.push({
            xAxis: selected,
            lineStyle: { color: selectedColor, width: 1, type: 'solid' },
            label: {
                show: true,
                formatter: `Futures ${formatOneDecimal(selected)}`,
                rotate: 90,
                position: 'insideEndTop',
                color: selectedColor,
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

function formatOneDecimal(value) {
    if (!Number.isFinite(value)) {
        return '0';
    }

    return Number(value).toFixed(1);
}

export function resetAutoScale(instanceId, resetXy, resetTime) {
    const instance = instances.get(instanceId);
    if (!instance) {
        return;
    }

    if (resetXy) {
        instance.currentRangeX = null;
        instance.currentRangeY = null;
    }
    if (resetTime) {
        instance.currentRangeTime = null;
    }
    if (resetXy || resetTime) {
        instance.preserveRange = false;
        if (instance.lastOption) {
            instance.chart.setOption(instance.lastOption, { notMerge: true, lazyUpdate: true });
        }
    }
}

function handleDomClick(instance, evt) {
    if (instance.isDragging || performance.now() < instance.suppressClickUntil) {
        return;
    }
    const rect = instance.chart.getDom().getBoundingClientRect();
    const point = [evt.clientX - rect.left, evt.clientY - rect.top];
    selectPriceAtPoint(instance, point);
}

function emitRangeChanged(instance) {
    const xRange = instance.currentRangeX ?? getVisibleRange(instance.chart, 'x', 0);
    const yRange = instance.currentRangeY ?? getVisibleRange(instance.chart, 'y', 0);
    const timeRange = instance.currentRangeTime ?? getVisibleRange(instance.chart, 'y', 1);
    if (!xRange || !yRange) {
        return;
    }

    instance.dotNetRef.invokeMethodAsync('OnRangeChanged', xRange.min, xRange.max, yRange.min, yRange.max);
    if (timeRange) {
        instance.dotNetRef.invokeMethodAsync('OnTimeRangeChanged', timeRange.min, timeRange.max);
    }
    instance.rangeOverride = null;
    instance.timeRangeOverride = null;
}

function getVisibleRange(chart, axis, axisIndex) {
    const option = chart.getOption();
    if (axis === 'x') {
        const xAxis = Array.isArray(option?.xAxis) ? option.xAxis[axisIndex] : option?.xAxis;
        if (xAxis && Number.isFinite(xAxis.min) && Number.isFinite(xAxis.max)) {
            return { min: xAxis.min, max: xAxis.max };
        }
    } else {
        const yAxis = Array.isArray(option?.yAxis) ? option.yAxis[axisIndex] : option?.yAxis;
        if (yAxis && Number.isFinite(yAxis.min) && Number.isFinite(yAxis.max)) {
            return { min: yAxis.min, max: yAxis.max };
        }
    }

    const axisComponent = chart.getModel().getComponent(`${axis}Axis`, axisIndex);
    if (!axisComponent) {
        return null;
    }

    const axisOption = axisComponent.option ?? {};
    if (Number.isFinite(axisOption.min) && Number.isFinite(axisOption.max)) {
        return { min: axisOption.min, max: axisOption.max };
    }

    const axisRuntime = axisComponent.axis;
    const scale = axisRuntime?.scale;
    if (!scale || typeof scale.getExtent !== 'function') {
        return null;
    }

    const extent = scale.getExtent();
    if (!Array.isArray(extent) || extent.length < 2 || !Number.isFinite(extent[0]) || !Number.isFinite(extent[1])) {
        return null;
    }

    return { min: extent[0], max: extent[1] };
}

function normalizeOption(option, instance) {
    const normalized = option ?? {};

    const xAxes = Array.isArray(normalized.xAxis) ? normalized.xAxis : (normalized.xAxis ? [normalized.xAxis] : []);
    const yAxes = Array.isArray(normalized.yAxis) ? normalized.yAxis : (normalized.yAxis ? [normalized.yAxis] : []);

    if (xAxes[0]?.axisLabel) {
        xAxes[0].axisLabel.formatter = (value) => String(value);
    }

    if (yAxes[0]?.axisLabel) {
        yAxes[0].axisLabel.formatter = (value) => Number(value).toFixed(2);
    }

    if (Array.isArray(normalized.xAxis)) {
        normalized.xAxis = xAxes;
    } else if (xAxes.length > 0) {
        normalized.xAxis = xAxes[0];
    }

    if (Array.isArray(normalized.yAxis)) {
        normalized.yAxis = yAxes;
    } else if (yAxes.length > 0) {
        normalized.yAxis = yAxes[0];
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
            if (seriesOption?.skipTooltip) {
                continue;
            }
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
            if (series.renderKind === 'tickerCandles') {
                series.renderItem = renderTickerCandles;
            }
            if (id.endsWith('-be-exp') || id.endsWith('-be-temp')) {
                series.label = series.label ?? {};
                series.label.formatter = (params) => formatPrice(Array.isArray(params.value) ? params.value[0] : params.value);
            }
        }
    }

    return normalized;
}

function renderTickerCandles(params, api) {
    const time = api.value(0);
    const open = api.value(1);
    const close = api.value(2);
    const low = api.value(3);
    const high = api.value(4);

    if (!Number.isFinite(time) || !Number.isFinite(open) || !Number.isFinite(close) || !Number.isFinite(low) || !Number.isFinite(high)) {
        return null;
    }

    const meta = candleMetaBySeries.get(params.seriesId);
    const bodyHeight = meta?.bodyHeight ?? 6;
    const coordSys = params.coordSys;
    const rawY = api.coord([open, time])[1];
    if (!coordSys || !Number.isFinite(rawY)) {
        return null;
    }

    // Allow edge candles to be fully visible by snapping within half height.
    const minY = coordSys.y + bodyHeight / 2;
    const maxY = coordSys.y + coordSys.height - bodyHeight / 2;
    if (rawY < coordSys.y - bodyHeight || rawY > coordSys.y + coordSys.height + bodyHeight) {
        return null;
    }

    const y = Math.min(Math.max(rawY, minY), maxY);
    const lowPoint = api.coord([low, time]);
    const highPoint = api.coord([high, time]);
    const openPoint = api.coord([open, time]);
    const closePoint = api.coord([close, time]);

    const left = Math.min(openPoint[0], closePoint[0]);
    const width = Math.max(1, Math.abs(closePoint[0] - openPoint[0]));
    const top = y - bodyHeight / 2;

    const isUp = close >= open;
    const color = isUp ? 'rgba(16,185,129,0.35)' : 'rgba(239,68,68,0.35)';
    const border = isUp ? 'rgba(16,185,129,0.55)' : 'rgba(239,68,68,0.55)';

    const lineShape = {
        type: 'line',
        shape: {
            x1: lowPoint[0],
            y1: y,
            x2: highPoint[0],
            y2: y
        },
        style: {
            stroke: border,
            lineWidth: 1
        }
    };

    const rectShape = {
        type: 'rect',
        shape: {
            x: left,
            y: top,
            width,
            height: bodyHeight
        },
        style: {
            fill: color,
            stroke: border,
            lineWidth: 1
        }
    };

    const group = {
        type: 'group',
        children: [lineShape, rectShape]
    };
    if (coordSys) {
        group.clipPath = {
            type: 'rect',
            shape: {
                x: coordSys.x,
                y: coordSys.y,
                width: coordSys.width,
                height: coordSys.height
            }
        };
    }

    return group;
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
            x: instance.currentRangeX ?? getVisibleRange(instance.chart, 'x', 0),
            y: instance.currentRangeY ?? getVisibleRange(instance.chart, 'y', 0),
            time: instance.currentRangeTime ?? getVisibleRange(instance.chart, 'y', 1)
        },
        rect: getGridRect(instance.chart),
        fixedYRange: null
    };
    if (mode === 'y-time') {
        const fixed = instance.axisDrag.startRange.y ?? getVisibleRange(instance.chart, 'y', 0);
        instance.axisDrag.fixedYRange = fixed;
        instance.pinnedRangeY = fixed;
    }
}

function handleAxisDragMove(instance, evt) {
    const point = getPoint(evt);
    const mode = getAxisDragMode(instance.chart, point);
    instance.chart.getZr().setCursorStyle(
        mode === 'x'
            ? 'ew-resize'
            : (mode === 'y' || mode === 'y-time')
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
        instance.currentRangeX = zoomed;
        instance.rangeOverride = { x: zoomed };
    } else if (drag.mode === 'y') {
        const deltaPixels = drag.startY - point[1];
        // Dragging up shrinks the range (max down, min up). Dragging down expands it.
        const zoomed = scaleRange(drag.startRange.y, -deltaPixels, drag.rect.height, 0.9);
        instance.chart.setOption({ yAxis: { min: zoomed.min, max: zoomed.max } }, { notMerge: false, lazyUpdate: true });
        instance.currentRangeY = zoomed;
        instance.rangeOverride = { y: zoomed };
    } else if (drag.mode === 'y-time') {
        const deltaPixels = point[1] - drag.startY;
        const deltaValue = pixelsToValueDelta(drag.startRange.time, deltaPixels, drag.rect.height);
        const zoomed = stretchRangeFromMax(drag.startRange.time, deltaValue);
        const yRange = drag.fixedYRange ?? instance.pinnedRangeY ?? drag.startRange.y ?? getVisibleRange(instance.chart, 'y', 0);
        instance.pinnedRangeY = yRange ?? instance.pinnedRangeY;
        instance.chart.setOption({
            yAxis: [
                yRange ? { min: yRange.min, max: yRange.max } : {},
                { min: zoomed.min, max: zoomed.max }
            ]
        }, { notMerge: false, lazyUpdate: true });
        instance.currentRangeTime = zoomed;
        instance.timeRangeOverride = zoomed;
        refreshCandleMeta(instance);
    } else if (drag.mode === 'plot') {
        const deltaPixelsX = point[0] - drag.startX;
        const deltaPixelsY = drag.startY - point[1];
        const deltaX = -pixelsToValueDelta(drag.startRange.x, deltaPixelsX, drag.rect.width);
        const deltaY = -pixelsToValueDelta(drag.startRange.y, deltaPixelsY, drag.rect.height);
        const shiftedX = shiftRange(drag.startRange.x, deltaX);
        const shiftedY = shiftRange(drag.startRange.y, deltaY);
        applyZoom(instance.chart, 'x', 0, shiftedX);
        instance.chart.setOption({ yAxis: { min: shiftedY.min, max: shiftedY.max } }, { notMerge: false, lazyUpdate: true });
        instance.currentRangeX = shiftedX;
        instance.currentRangeY = shiftedY;
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
    if (instance.axisDrag && (instance.rangeOverride || instance.timeRangeOverride)) {
        instance.currentRangeX = getVisibleRange(instance.chart, 'x', 0);
        if (instance.timeRangeOverride && !instance.rangeOverride && instance.axisDrag?.fixedYRange) {
            instance.currentRangeY = instance.axisDrag.fixedYRange;
        } else {
            instance.currentRangeY = getVisibleRange(instance.chart, 'y', 0);
        }
        instance.currentRangeTime = getVisibleRange(instance.chart, 'y', 1);
        instance.preserveRange = true;
    if (instance.currentRangeX || instance.currentRangeY || instance.currentRangeTime) {
        const hasTimeAxis = Array.isArray(instance.lastOption?.yAxis) && instance.lastOption.yAxis.length > 1;
        const yAxisOption = hasTimeAxis && instance.currentRangeTime
            ? [
                { min: instance.currentRangeY?.min, max: instance.currentRangeY?.max },
                { min: instance.currentRangeTime.min, max: instance.currentRangeTime.max }
            ]
            : { min: instance.currentRangeY?.min, max: instance.currentRangeY?.max };
        instance.chart.setOption({
            xAxis: instance.currentRangeX ? { min: instance.currentRangeX.min, max: instance.currentRangeX.max } : {},
            yAxis: yAxisOption,
             
        }, { notMerge: false, lazyUpdate: true });
    }
        emitRangeChanged(instance);
    }
    if (instance.clickState?.moved || instance.isDragging) {
        instance.suppressClickUntil = performance.now() + 250;
    }
    instance.axisDrag = null;
    instance.clickState = null;
    instance.isDragging = false;
    instance.pinnedRangeY = null;
    instance.chart.getZr().setCursorStyle('default');

    if (instance.tooltipHidden) {
        instance.tooltipHidden = false;
        instance.chart.setOption({ tooltip: { show: true } }, { notMerge: false, lazyUpdate: true });
    }

    refreshCandleMeta(instance);
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
    const axisBandYRight = { min: rect.y, max: rect.y + rect.height, left: rect.x + rect.width - 6, right: rect.x + rect.width + 54 };

    if (x >= axisBandX.min && x <= axisBandX.max && y >= axisBandX.top && y <= axisBandX.bottom) {
        return 'x';
    }

    if (y >= axisBandY.min && y <= axisBandY.max && x >= axisBandY.left && x <= axisBandY.right) {
        return 'y';
    }

    if (y >= axisBandYRight.min && y <= axisBandYRight.max && x >= axisBandYRight.left && x <= axisBandYRight.right) {
        return 'y-time';
    }

    if (x >= rect.x && x <= rect.x + rect.width && y >= rect.y && y <= rect.y + rect.height) {
        return 'plot';
    }

    return null;
}

function getGridRect(chart) {
    const grid = chart.getModel().getComponent('grid', 0);
    const coord = grid?.coordinateSystem;
    if (!coord || typeof coord.getRect !== 'function') {
        return null;
    }
    return coord.getRect();
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
        chart.setOption({ xAxis: { min: range.min, max: range.max } }, { notMerge: false, lazyUpdate: true });
    } else {
        chart.setOption({ yAxis: { min: range.min, max: range.max } }, { notMerge: false, lazyUpdate: true });
    }
}

function syncAxisRanges(instance, option) {
    if (!instance || !option) {
        return;
    }

    if (instance.preserveRange) {
        return;
    }

    const xAxis = Array.isArray(option.xAxis) ? option.xAxis[0] : option.xAxis;
    if (xAxis && Number.isFinite(xAxis.min) && Number.isFinite(xAxis.max)) {
        instance.currentRangeX = { min: xAxis.min, max: xAxis.max };
    }

    const yAxis = Array.isArray(option.yAxis) ? option.yAxis[0] : option.yAxis;
    if (yAxis && Number.isFinite(yAxis.min) && Number.isFinite(yAxis.max)) {
        instance.currentRangeY = { min: yAxis.min, max: yAxis.max };
    }

    const timeAxis = Array.isArray(option.yAxis) && option.yAxis.length > 1 ? option.yAxis[1] : null;
    if (timeAxis && Number.isFinite(timeAxis.min) && Number.isFinite(timeAxis.max)) {
        instance.currentRangeTime = { min: timeAxis.min, max: timeAxis.max };
    } else {
        instance.currentRangeTime = null;
    }
}

function refreshCandleMetaFromOption(instance, option) {
    if (!option || !Array.isArray(option.series)) {
        return;
    }

    const candleSeries = option.series.find((s) => s && s.id === '__ticker_candles__');
    if (!candleSeries || !Array.isArray(candleSeries.data)) {
        candleMetaBySeries.delete('__ticker_candles__');
        return;
    }

    refreshCandleMeta(instance, candleSeries.data);
}

function refreshCandleMeta(instance, candleData) {
    if (!instance || !instance.lastOption) {
        return;
    }

    const hasTimeAxis = Array.isArray(instance.lastOption?.yAxis) && instance.lastOption.yAxis.length > 1;
    if (!hasTimeAxis) {
        candleMetaBySeries.delete('__ticker_candles__');
        return;
    }

    const data = Array.isArray(candleData)
        ? candleData
        : (instance.lastOption?.series?.find((s) => s && s.id === '__ticker_candles__')?.data ?? []);
    if (!Array.isArray(data) || data.length === 0) {
        candleMetaBySeries.delete('__ticker_candles__');
        return;
    }

    const range = instance.currentRangeTime ?? getVisibleRange(instance.chart, 'y', 1);
    if (!range || !Number.isFinite(range.min) || !Number.isFinite(range.max)) {
        return;
    }

    const rect = getGridRect(instance.chart);
    if (!rect) {
        return;
    }

    const min = Math.min(range.min, range.max);
    const max = Math.max(range.min, range.max);
    let visibleCount = 0;
    for (const candle of data) {
        const time = candle?.[0];
        if (Number.isFinite(time) && time >= min && time <= max) {
            visibleCount++;
        }
    }

    const spacing = rect.height / Math.max(1, visibleCount);
    const bodyHeight = Math.max(2, Math.min(12, spacing * 0.6));
    candleMetaBySeries.set('__ticker_candles__', { bodyHeight });
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
        instance.selectedPrice = xValue;
        applyMarkers(instance);
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
