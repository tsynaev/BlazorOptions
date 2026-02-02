window.payoffChart = {
    _instances: new Set(),
    _resizeTimer: null,
    _resizeScheduled: false,
    _ensureResizeListener: function () {
        if (window.payoffChart._resizeScheduled) {
            return;
        }
        window.payoffChart._resizeScheduled = true;
        const schedule = () => {
            if (window.payoffChart._resizeTimer) {
                clearTimeout(window.payoffChart._resizeTimer);
            }
            window.payoffChart._resizeTimer = setTimeout(() => {
                window.payoffChart._resizeTimer = null;
                window.payoffChart._instances.forEach((element) => {
                    const instance = element && element.__payoffInstance;
                    if (element && element.isConnected && instance && !instance.isDisposed?.()) {
                        instance.resize();
                    }
                });
            }, 80);
        };
        window.addEventListener('resize', schedule);
        window.addEventListener('orientationchange', schedule);
        if (window.matchMedia) {
            const media = window.matchMedia('(max-width: 600px)');
            const handler = () => schedule();
            if (media.addEventListener) {
                media.addEventListener('change', handler);
            } else if (media.addListener) {
                media.addListener(handler);
            }
        }
    },
    render: function (element, options, dotNetHelper) {
        if (!element || !options) return;

        if (!element.isConnected) {
            if (element.__payoffInstance && !element.__payoffInstance.isDisposed?.()) {
                element.__payoffInstance.dispose();
            }
            element.__payoffInstance = null;
            window.payoffChart._instances.delete(element);
            if (element.__payoffResizeObserver) {
                element.__payoffResizeObserver.disconnect();
                element.__payoffResizeObserver = null;
            }
            if (element.__payoffResizeRaf) {
                cancelAnimationFrame(element.__payoffResizeRaf);
                element.__payoffResizeRaf = null;
            }
            return;
        }

        if (!element || typeof element.offsetWidth !== 'number' || typeof element.offsetHeight !== 'number') {
            return;
        }
        const hasSize = element.offsetWidth > 0 || element.offsetHeight > 0;
        if (!hasSize) {
            const attempts = (element.__payoffRenderAttempts || 0) + 1;
            element.__payoffRenderAttempts = attempts;
            if (attempts <= 5) {
                requestAnimationFrame(() => window.payoffChart.render(element, options, dotNetHelper));
            }
            return;
        }

        element.__payoffRenderAttempts = 0;

        if (element.__payoffInstance?.isDisposed?.()) {
            element.__payoffInstance = null;
        } else if (element.__payoffInstance?.getDom?.() && element.__payoffInstance.getDom() !== element) {
            element.__payoffInstance.dispose();
            element.__payoffInstance = null;
        }

        const interactionHoldMs = 200;
        const lastInteractionAt = element.__payoffLastInteractionAt || 0;
        if (element.__payoffIsInteracting || (Date.now() - lastInteractionAt) < interactionHoldMs) {
            return;
        }

        const chart = element.__payoffInstance || echarts.init(element);
        element.__payoffInstance = chart;
        window.payoffChart._instances.add(element);
        window.payoffChart._ensureResizeListener();
        const ensureResizeObserver = () => {
            if (element.__payoffResizeObserver || typeof ResizeObserver !== 'function') {
                return;
            }
            const observer = new ResizeObserver(() => {
                if (!element.isConnected) {
                    observer.disconnect();
                    element.__payoffResizeObserver = null;
                    return;
                }
                if (element.__payoffResizeRaf) {
                    return;
                }
                element.__payoffResizeRaf = requestAnimationFrame(() => {
                    element.__payoffResizeRaf = null;
                    const instance = element.__payoffInstance;
                    if (instance && !instance.isDisposed?.()) {
                        instance.resize();
                    }
                });
            });
            element.__payoffResizeObserver = observer;
            observer.observe(element);
        };
        ensureResizeObserver();

        const labels = options.labels || [];
        const collections = options.collections || [];
        const activeCollectionId = options.activeCollectionId;
        const prices = (options.prices && options.prices.length ? options.prices : labels) || [];
        const yMin = options.yMin ?? 'dataMin';
        const yMax = options.yMax ?? 'dataMax';
        const positionKey = options.positionId || 'default';
        const tempPrice = options.temporaryPrice;
        const visibleCollections = collections.filter(collection => collection.isVisible !== false);
        const collectionNameById = new Map();
        const collectionById = new Map();
        const legendData = [];
        const legendSelected = {};
        collections.forEach((collection) => {
            const id = String(collection.collectionId);
            collectionNameById.set(id, collection.name);
            collectionById.set(id, collection);
            legendData.push({
                name: id,
                icon: 'circle',
                itemStyle: {
                    color: collection.color,
                    borderColor: collection.color
                },
                lineStyle: {
                    color: collection.color
                }
            });
            legendSelected[id] = collection.isVisible !== false;
        });

        const priceRangeKey = `payoffChart:${positionKey}:priceRange`;
        const pnlRangeKey = `payoffChart:${positionKey}:pnlRange`;
        const numericPrices = prices.map(label => Number(label));
        const ySpan = Math.abs(Number(yMax) - Number(yMin)) || 1;
        const paddedYMin = typeof yMin === 'number' ? yMin - ySpan * 0.5 : yMin;
        const paddedYMax = typeof yMax === 'number' ? yMax + ySpan * 0.5 : yMax;
        const priceCount = numericPrices.length;
        const defaultPriceRange = {
            start: priceCount > 0 ? numericPrices[0] : 0,
            end: priceCount > 0 ? numericPrices[priceCount - 1] : 0
        };
        const defaultPnlRange = { start: paddedYMin, end: paddedYMax };

        const loadRange = (key) => {
            try {
                const raw = localStorage.getItem(key);
                if (!raw) return null;
                const parsed = JSON.parse(raw);
                const start = Number(parsed.start);
                const end = Number(parsed.end);

                if (Number.isFinite(start) && Number.isFinite(end)) {
                    return { start, end };
                }
            } catch { /* ignore */ }

            return null;
        };

        const priceRange = loadRange(priceRangeKey);
        const pnlRange = loadRange(pnlRangeKey);

        const ensureAxisRangeState = () => {
            if (!element.__payoffAxisRange) {
                element.__payoffAxisRange = {};
            }
        };

        const toNumericRange = (value) => {
            if (!value) {
                return null;
            }

            const start = Number(value.start);
            const end = Number(value.end);

            if (!Number.isFinite(start) || !Number.isFinite(end)) {
                return null;
            }

            return { start, end };
        };

        const cloneRange = (range) => ({ start: Number(range.start), end: Number(range.end) });

        const setAxisRange = (key, range) => {
            if (!range) {
                return;
            }

            ensureAxisRangeState();
            element.__payoffAxisRange[key] = range;
        };

        const getStoredAxisRange = (key) => {
            ensureAxisRangeState();
            return toNumericRange(element.__payoffAxisRange[key]);
        };

        setAxisRange('price', toNumericRange(priceRange) ?? cloneRange(defaultPriceRange));
        setAxisRange('pnl', toNumericRange(pnlRange) ?? cloneRange(defaultPnlRange));

        const priceZoomInside = {
            type: 'inside',
            xAxisIndex: 0,
            filterMode: 'filter',
            zoomOnMouseWheel: false,
            moveOnMouseMove: true,
            startValue: priceRange?.start,
            endValue: priceRange?.end
        };

        const pnlZoomInside = {
            type: 'inside',
            yAxisIndex: 0,
            filterMode: 'none',
            zoomOnMouseWheel: false,
            moveOnMouseMove: true,
            zoomLock: false,
            startValue: pnlRange?.start ?? paddedYMin,
            endValue: pnlRange?.end ?? paddedYMax
        };

        const formatPrice = (value) => {
            const numeric = Number(value);
            if (!Number.isFinite(numeric)) return '';
            const abs = Math.abs(numeric);

            if (abs === 0) return '0';

            if (abs >= 100) {
                return numeric.toFixed(2).replace(/\.?0+$/, '');
            }

            const magnitude = Math.floor(Math.log10(abs));
            const decimals = magnitude >= 0 ? Math.max(0, 2 - magnitude) : Math.abs(magnitude) + 2;

            return numeric.toFixed(decimals).replace(/\.?0+$/, '');
        };

        const normalizePrice = (value) => {
            const numeric = Number(value);
            if (!Number.isFinite(numeric)) return null;
            const abs = Math.abs(numeric);
            if (abs >= 100) {
                return Math.round(numeric);
            }
            if (abs === 0) {
                return 0;
            }
            const magnitude = Math.floor(Math.log10(abs));
            const decimals = magnitude >= 0
                ? Math.max(0, 2 - magnitude)
                : Math.abs(magnitude) + 2;
            return Number(numeric.toFixed(decimals));
        };

        const activeCollectionKey = activeCollectionId ? String(activeCollectionId) : null;
        const activeCollection = visibleCollections.find(collection => String(collection.collectionId) === activeCollectionKey)
            || visibleCollections[0]
            || collections.find(collection => String(collection.collectionId) === activeCollectionKey)
            || collections[0];
        element.__payoffActiveCollectionId = activeCollection ? String(activeCollection.collectionId) : null;
        element.__payoffFormatPrice = formatPrice;

        const buildPoints = (values) => numericPrices.map((price, index) => {
            const value = values && values.length > index ? values[index] : null;
            return [price, Number.isFinite(value) ? value : null];
        });

        const activeExpiryPoints = activeCollection ? buildPoints(activeCollection.expiryProfits) : [];
        const activeTheoreticalPoints = activeCollection ? buildPoints(activeCollection.theoreticalProfits) : [];
        const activeTempPoint = activeCollection && Number.isFinite(tempPrice) && Number.isFinite(activeCollection.temporaryPnl)
            ? [{ value: [Number(tempPrice), Number(activeCollection.temporaryPnl)], symbolSize: 9 }]
            : [];
        const activeTempExpiryPoint = activeCollection && Number.isFinite(tempPrice) && Number.isFinite(activeCollection.temporaryExpiryPnl)
            ? [{ value: [Number(tempPrice), Number(activeCollection.temporaryExpiryPnl)], symbolSize: 9 }]
            : [];

        const indexMarkLine = Number.isFinite(tempPrice)
            ? {
                silent: true,
                symbol: 'none',
                z: 3,
                lineStyle: { color: '#9E9E9E', width: 1.5, type: 'dashed' },
                label: {
                    show: true,
                    formatter: function () { return `Future: ${formatPrice(tempPrice)}`; },
                    rotate: 90,
                    position: 'insideEndTop',
                    align: 'center',
                    verticalAlign: 'top',
                    distance: 10,
                    fontSize: 9,
                    color: '#CCCCCC',

                },
                data: [
                    { xAxis: Number(tempPrice) }
                ]
            }
            : null;

        const findBreakEvens = (points) => {
            const values = [];
            for (let i = 1; i < points.length; i++) {
                const [prevPrice, prevProfit] = points[i - 1];
                const [currPrice, currProfit] = points[i];

                if (prevProfit === undefined || currProfit === undefined) continue;
                if ((prevProfit <= 0 && currProfit >= 0) || (prevProfit >= 0 && currProfit <= 0)) {
                    const deltaProfit = currProfit - prevProfit;
                    const ratio = deltaProfit === 0 ? 0 : -prevProfit / deltaProfit;
                    const price = prevPrice + (currPrice - prevPrice) * ratio;

                    if (Number.isFinite(price)) {
                        values.push(price);
                    }
                }
            }
            return values;
        };

        const breakEvens = findBreakEvens(activeExpiryPoints);
        const tempBreakEvens = findBreakEvens(activeTheoreticalPoints);

        const series = [];
        collections.forEach((collection) => {
            const seriesName = String(collection.collectionId);
            const expiryPoints = buildPoints(collection.expiryProfits);
            const theoreticalPoints = buildPoints(collection.theoreticalProfits);
            const isActive = activeCollection && String(collection.collectionId) === String(activeCollection.collectionId);

            series.push({
                name: seriesName,
                id: `${seriesName}:expiry`,
                type: 'line',
                smooth: true,
                data: expiryPoints,
                symbol: 'none',
                lineStyle: {
                    width: 3,
                    color: collection.color,
                    type: 'solid'
                },
                z: 2,
                markLine: isActive ? indexMarkLine || undefined : undefined
            });

            series.push({
                name: seriesName,
                id: `${seriesName}:temp`,
                type: 'line',
                smooth: true,
                data: theoreticalPoints,
                symbol: 'none',
                lineStyle: {
                    color: collection.color,
                    width: 2,
                    type: 'dashed'
                },
                z: 1,
                zlevel: 0
            });

            if (isActive && activeTempPoint.length > 0) {
                series.push({
                    name: 'Temp P/L',
                    type: 'scatter',
                    data: activeTempPoint,
                    symbolSize: 9,
                    itemStyle: { color: collection.color },
                    label: {
                        show: true,
                        formatter: function (params) {
                            if (!params || !params.value || params.value.length < 2) return '';
                            return `${Number(params.value[1]).toFixed(2)}`;
                        },
                        position: 'right',
                        offset: [8, -6],
                        fontSize: 9,
                        color: collection.color,
                       // textBorderColor: '#FFFFFF',
                      //  textBorderWidth: 1
                    }
                });
            }

            if (isActive && activeTempExpiryPoint.length > 0) {
                series.push({
                    name: 'Expiry P/L',
                    type: 'scatter',
                    data: activeTempExpiryPoint,
                    symbolSize: 9,
                    itemStyle: { color: collection.color },
                    label: {
                        show: true,
                        formatter: function (params) {
                            if (!params || !params.value || params.value.length < 2) return '';
                            return `${Number(params.value[1]).toFixed(2)}`;
                        },
                        position: 'right',
                        offset: [8, -6],
                        fontSize: 9,
                        color: collection.color,
                    }
                });
            }

            if (isActive && breakEvens.length > 0) {
                series.push({
                    name: 'Break-even',
                    type: 'scatter',
                    z: 10,
                    zlevel: 2,
                    data: breakEvens.map(price => ({
                        value: [price, 0],
                        symbolSize: 12,
                        itemStyle: { color: '#FFC107', borderColor: '#FFF8E1', borderWidth: 2 },
                        label: {
                            show: true,
                            formatter: function (params) { return formatPrice(params.value[0]); },
                            position: 'top',
                            offset: [0, -14],
                            fontSize: 10,
                            color: '#FFFDE7',
                            textBorderColor: '#000000',
                            textBorderWidth: 3,
                            backgroundColor: 'rgba(0, 0, 0, 0.65)',
                            padding: [4, 6, 2, 6]
                        }
                    })),
                    tooltip: { show: false }
                });
            }

            if (isActive && tempBreakEvens.length > 0) {
                series.push({
                    name: 'Temp Break-even',
                    type: 'scatter',
                    z: 10,
                    zlevel: 2,
                    data: tempBreakEvens.map(price => ({
                        value: [price, 0],
                        symbolSize: 11,
                        itemStyle: { color: '#29B6F6', borderColor: '#E1F5FE', borderWidth: 2 },
                        label: {
                            show: true,
                            formatter: function (params) { return formatPrice(params.value[0]); },
                            position: 'top',
                            offset: [0, -14],
                            fontSize: 10,
                            color: '#E1F5FE',
                            textBorderColor: '#0D47A1',
                            textBorderWidth: 3,
                            backgroundColor: 'rgba(13, 71, 161, 0.65)',
                            padding: [4, 6, 2, 6]
                        }
                    })),
                    tooltip: { show: false }
                });
            }
        });

        if (!element || typeof element.offsetWidth !== 'number') {
            return;
        }
        const isNarrow = element.offsetWidth <= 480;
        const axisFontSize = isNarrow ? 8 : 10;
        const gridLeft = isNarrow ? 35 : 35;
        const gridRight = isNarrow ?10 : 10;
        const container = element.__payoffContainer || element;
        if (container && container.style) {
            container.style.touchAction = 'none';
            container.style.userSelect = 'none';
            element.__payoffContainer = container;
        }

        chart.setOption({
            grid: { left: gridLeft, right: gridRight, top: 60, bottom: 50 },
            legend: {
                top: 0,
                left: 10,
                data: legendData,
                selected: legendSelected,
                textStyle: {
                    color: '#E0E0E0'
                },
                formatter: function (name) {
                    return collectionNameById.get(name) || name;
                }
            },
            toolbox: {
                feature: {
                    restore: {},
                    dataZoom: { yAxisIndex: false }
                },
                right: 10
            },
            tooltip: {
                trigger: 'axis',
                renderMode: 'richText',
                axisPointer: {
                    type: 'cross',
                    snap: true,
                    label: {
                        show: true,
                        backgroundColor: '#455A64',
                        formatter: function (params) {
                            const numeric = Number(params.value);
                            if (!Number.isFinite(numeric)) return '';

                            if (params.axisDimension === 'x') {
                                return formatPrice(numeric);
                            }

                            if (params.axisDimension === 'y') {
                                return numeric.toFixed(2);
                            }

                            return numeric.toFixed(2);
                        }
                    }
                },
                formatter: function (params) {
                    if (!params || !params.length) return '';

                    const firstValid = params.find(p => p && p.value);
                    const price = firstValid && firstValid.value && firstValid.value.length ? Number(firstValid.value[0]) : Number(firstValid.axisValue);

                    if (!Number.isFinite(price)) return '';

                    const seriesLines = [];
                    params.forEach(p => {
                        if (!p || !p.seriesName) return;
                        const value = Array.isArray(p.value) ? p.value[1] : (p.data && p.data.value ? p.data.value[1] : undefined);
                        if (value === null || value === undefined || Number.isNaN(value)) return;
                        const baseName = collectionNameById.get(p.seriesName) || p.seriesName;
                        let label = baseName;
                        if (p.seriesId && p.seriesId.endsWith(':expiry')) {
                            label = `${baseName} Expiry`;
                        } else if (p.seriesId && p.seriesId.endsWith(':temp')) {
                            label = `${baseName} Temp`;
                        }
                        seriesLines.push(`${label}: ${Number(value).toFixed(2)}`);
                    });

                    const lines = [`Price: ${formatPrice(price)}`];
                    seriesLines.forEach(line => lines.push(line));

                    return lines.join('\n');
                }
            },
            xAxis: {
                type: 'value',
                min: 'dataMin',
                max: 'dataMax',
              //  boundaryGap: ['2%', '2%'],
                axisLabel: {
                    formatter: function (value) {
                        return formatPrice(value);
                    },
                    color: '#B0BEC5',
                    fontSize: axisFontSize
                },
                splitLine: { lineStyle: { color: 'rgba(224, 224, 224, 0.1)' } },
                axisPointer: {
                    show: true,
                    snap: true,
                    label: {
                        show: true,
                        formatter: function (params) {
                            return formatPrice(params.value);
                        },
                        backgroundColor: '#7581BD'
                    }
                }
            },
            yAxis: {
                type: 'value',
                min: paddedYMin,
                max: paddedYMax,
                boundaryGap: ['10%', '10%'],
                scale: true,
                axisLabel: {
                    formatter: function (value) {
                        return value.toFixed(0);
                    },
                    color: '#B0BEC5',
                    fontSize: axisFontSize
                },
                splitLine: { lineStyle: { color: 'rgba(224, 224, 224, 0.1)' } },
                axisPointer: {
                    show: true,
                    snap: true,
                    label: {
                        formatter: function (params) {
                            const numeric = Number(params.value);
                            return Number.isFinite(numeric) ? numeric.toFixed(2) : '';
                        }
                    }
                }
            },
            dataZoom: [
                priceZoomInside,
                pnlZoomInside
            ],
            series: series
        }, true);

        const persistRange = (key, range) => {
            try {
                localStorage.setItem(key, JSON.stringify(range));
            } catch { /* ignore */ }
        };

        const rangeSaveDelay = 200;
        const pendingRangeUpdates = new Map();
        let rangeSaveTimerId;

        const scheduleRangeSave = (key, range) => {
            pendingRangeUpdates.set(key, range);
            if (rangeSaveTimerId) {
                clearTimeout(rangeSaveTimerId);
            }
            rangeSaveTimerId = setTimeout(() => {
                pendingRangeUpdates.forEach((value, targetKey) => {
                    persistRange(targetKey, value);
                });
                pendingRangeUpdates.clear();
                rangeSaveTimerId = undefined;
            }, rangeSaveDelay);
        };

        const toRange = (zoomEntry, fallbackRange) => {
            if (!zoomEntry) return fallbackRange;
            const start = Number(zoomEntry.startValue ?? zoomEntry.start);
            const end = Number(zoomEntry.endValue ?? zoomEntry.end);

            if (Number.isFinite(start) && Number.isFinite(end)) {
                return { start, end };
            }

            return fallbackRange;
        };

        chart.off('dataZoom');
        chart.on('dataZoom', () => {
            const now = Date.now();
            element.__payoffLastZoomAt = now;
            element.__payoffLastInteractionAt = now;
            const zoomState = chart.getOption().dataZoom || [];
            const priceZoom = zoomState.find(z => z.xAxisIndex !== undefined);
            const pnlZoom = zoomState.find(z => z.yAxisIndex !== undefined);

            const firstLabel = numericPrices.length > 0 ? numericPrices[0] : 0;
            const lastLabel = numericPrices.length > 0 ? numericPrices[numericPrices.length - 1] : firstLabel;

            const defaultPriceRange = priceRange ?? { start: firstLabel, end: lastLabel };
            const defaultPnlRange = pnlRange ?? { start: paddedYMin, end: paddedYMax };

            const priceSelection = toRange(priceZoom, defaultPriceRange);
            const pnlSelection = toRange(pnlZoom, defaultPnlRange);
            const normalizedPriceSelection = toNumericRange(priceSelection);
            if (normalizedPriceSelection) {
                scheduleRangeSave(priceRangeKey, normalizedPriceSelection);
                setAxisRange('price', normalizedPriceSelection);
            }

            const normalizedPnlSelection = toNumericRange(pnlSelection);
            if (normalizedPnlSelection) {
                scheduleRangeSave(pnlRangeKey, normalizedPnlSelection);
                setAxisRange('pnl', normalizedPnlSelection);
            }
        });

        chart.off('legendselectchanged');
        chart.on('legendselectchanged', (event) => {
            if (!dotNetHelper || !event || !event.name || !event.selected) {
                return;
            }

            const isVisible = event.selected[event.name];
            if (typeof isVisible === 'boolean') {
                dotNetHelper.invokeMethodAsync('OnLegendSelectionChanged', event.name, isVisible);
            }
        });

        chart.off('showTip');
        chart.off('hideTip');
        chart.on('showTip', () => {
            element.__payoffIsTooltipVisible = true;
        });
        chart.on('hideTip', () => {
            element.__payoffIsTooltipVisible = false;
        });

        chart.off('click');
        chart.getZr().off('click');
        chart.getZr().off('mousedown');
        chart.getZr().off('mousemove');
        chart.getZr().off('mouseup');
        chart.getZr().off('pointerdown');
        chart.getZr().off('pointermove');
        chart.getZr().off('pointerup');
        chart.getZr().off('globalout');
        if (element.__payoffDomClick) {
            element.removeEventListener('click', element.__payoffDomClick);
            element.__payoffDomClick = null;
        }

        const getGridRect = () => {
            try {
                const grid = chart.getModel().getComponent('grid', 0);
                return grid?.coordinateSystem?.getRect?.();
            } catch {
                return null;
            }
        };

        const clampRange = (minValue, maxValue, hardMin, hardMax) => {
            if (!Number.isFinite(minValue) || !Number.isFinite(maxValue)) {
                return { start: hardMin, end: hardMax };
            }

            const safeMin = Number(hardMin);
            const safeMax = Number(hardMax);
            const span = Math.max(maxValue - minValue, 0.0001);
            const center = (minValue + maxValue) / 2;
            let start = center - span / 2;
            let end = center + span / 2;

            if (Number.isFinite(safeMin) && Number.isFinite(safeMax) && safeMax > safeMin) {
                const allowedSpan = safeMax - safeMin;
                if (span < allowedSpan) {
                    if (start < safeMin) {
                        start = safeMin;
                        end = safeMin + span;
                    }

                    if (end > safeMax) {
                        end = safeMax;
                        start = safeMax - span;
                    }
                }
            }

            return { start, end };
        };

        const getZoomRange = (axis) => {
            const axisKey = axis === 'x' ? 'price' : 'pnl';
            const storedRange = getStoredAxisRange(axisKey);
            if (storedRange) {
                return storedRange;
            }

            const zoomState = chart.getOption().dataZoom || [];
            const zoomEntry = axis === 'x'
                ? zoomState.find(z => z.xAxisIndex !== undefined)
                : zoomState.find(z => z.yAxisIndex !== undefined);

            const defaultRange = axis === 'x' ? defaultPriceRange : defaultPnlRange;

            return toRange(zoomEntry, defaultRange);
        };

        const applyAxisZoom = (axis, range) => {
            const zoomIndex = axis === 'x' ? 0 : 1;
            const axisKey = axis === 'x' ? 'price' : 'pnl';
            setAxisRange(axisKey, range);
            chart.dispatchAction({
                type: 'dataZoom',
                dataZoomIndex: zoomIndex,
                startValue: range.start,
                endValue: range.end
            });
        };

        if (dotNetHelper) {
            const invokeSelection = (price) => {
                if (element.__payoffAxisDragState?.isDragging) {
                    return;
                }
                const lastZoomAt = element.__payoffLastZoomAt ?? 0;
                if (Date.now() - lastZoomAt < 150) {
                    return;
                }
                const normalized = normalizePrice(price);
                if (Number.isFinite(normalized) && normalized !== element.__payoffLastPrice) {
                    element.__payoffLastPrice = normalized;
                    dotNetHelper.invokeMethodAsync('OnChartPriceSelected', normalized);
                }
            };

            const pickPriceFromPixels = (x, y) => {
                const xValue = Number(x);
                const yValue = Number.isFinite(y) ? Number(y) : chart.getHeight() / 2;
                const fallbackY = Number.isFinite(yValue) ? yValue : 0;
                const coords = chart.convertFromPixel({ gridIndex: 0 }, [xValue, fallbackY])
                    ?? chart.convertFromPixel({ xAxisIndex: 0 }, [xValue, fallbackY]);
                let price = Array.isArray(coords) ? Number(coords[0]) : Number(coords);

                if (Number.isFinite(price)) {
                    return price;
                }

                if (!numericPrices.length) {
                    return null;
                }

                const minPricePixel = chart.convertToPixel({ xAxisIndex: 0 }, numericPrices[0]);
                const maxPricePixel = chart.convertToPixel({ xAxisIndex: 0 }, numericPrices[numericPrices.length - 1]);

                if (!Number.isFinite(minPricePixel) || !Number.isFinite(maxPricePixel)) {
                    return null;
                }

                const lower = Math.min(minPricePixel, maxPricePixel);
                const upper = Math.max(minPricePixel, maxPricePixel);
                const clampedX = Math.min(Math.max(x, lower), upper);
                const safeY = Number.isFinite(yValue) ? yValue : chart.getHeight() / 2;
                const clampedCoords = chart.convertFromPixel({ gridIndex: 0 }, [clampedX, safeY])
                    ?? chart.convertFromPixel({ xAxisIndex: 0 }, [clampedX, safeY]);

                return Array.isArray(clampedCoords) && clampedCoords.length > 0
                    ? Number(clampedCoords[0])
                    : null;
            };

            chart.on('click', (params) => {
                const extractValue = (value) => {
                    if (Array.isArray(value)) {
                        return Number(value[0]);
                    }

                    if (value !== undefined && value !== null) {
                        const numeric = Number(value);
                        return Number.isFinite(numeric) ? numeric : null;
                    }

                    return null;
                };

                const fromParams = extractValue(params?.value)
                    ?? extractValue(params?.data?.value);

                let price = fromParams;

                if (!Number.isFinite(price) && params?.event) {
                    price = pickPriceFromPixels(params.event.offsetX, params.event.offsetY ?? 0);
                }

                invokeSelection(price);
            });

            chart.getZr().on('click', (event) => {
                if (element.__payoffAxisDragState?.isDragging) {
                    return;
                }
                const price = pickPriceFromPixels(event.offsetX, event.offsetY ?? 0);
                invokeSelection(price);
            });

        }

        const axisDragPaddingX = 80;
        const axisDragPaddingY = 60;
        const axisZoomSpeed = 1.6;

        const handleAxisDown = (event) => {
            if (!event) return;
            const rect = getGridRect();
            if (!rect) return;

            const x = event.offsetX ?? 0;
            const y = event.offsetY ?? 0;
            const withinYBounds = y >= rect.y && y <= rect.y + rect.height;
            const withinXBounds = x >= rect.x && x <= rect.x + rect.width;

            const onYAxis = withinYBounds && x >= rect.x - axisDragPaddingX && x <= rect.x;
            const onXAxis = withinXBounds && y >= rect.y + rect.height && y <= rect.y + rect.height + axisDragPaddingY;

            if (!onYAxis && !onXAxis) {
                return;
            }

            const axis = onYAxis ? 'y' : 'x';
            const currentRange = getZoomRange(axis);
            if (!currentRange) return;

            if (event.cancelable) {
                event.preventDefault();
            }
            element.__payoffAxisDragState = {
                axis,
                startX: x,
                startY: y,
                rect,
                startRange: currentRange,
                isDragging: true
            };
            element.__payoffIsInteracting = true;
        };

        const setCursor = (value) => {
            if (!chart || chart.isDisposed?.()) {
                return;
            }
            const zr = chart.getZr?.();
            if (zr?.setCursorStyle) {
                zr.setCursorStyle(value || 'default');
                return;
            }
            const dom = chart.getDom?.();
            if (!dom) return;
            const cursorValue = value || '';
            if (dom.style.cursor !== cursorValue) {
                dom.style.cursor = cursorValue;
            }
        };

        const handleAxisMove = (event) => {
            const rect = getGridRect();
            const hasGrid = rect && event;
            if (hasGrid) {
                const x = event.offsetX;
                const y = event.offsetY;
                const withinYBounds = y >= rect.y && y <= rect.y + rect.height;
                const withinXBounds = x >= rect.x && x <= rect.x + rect.width;

                const onYAxis = withinYBounds && x >= rect.x - axisDragPaddingX && x <= rect.x;
                const onXAxis = withinXBounds && y >= rect.y + rect.height && y <= rect.y + rect.height + axisDragPaddingY;

                if (onYAxis) {
                    setCursor('ns-resize');
                } else if (onXAxis) {
                    setCursor('ew-resize');
                } else {
                    setCursor('');
                }
            } else {
                setCursor('');
            }

            const dragState = element.__payoffAxisDragState;
            if (hasGrid && !dragState?.isDragging) {
                chart.dispatchAction({ type: 'showTip', x: event.offsetX, y: event.offsetY });
            } else {
                chart.dispatchAction({ type: 'hideTip' });
            }

            if (!dragState || !dragState.isDragging) {
                return;
            }

            element.__payoffLastInteractionAt = Date.now();
            const activeRect = dragState.rect || rect || getGridRect();
            if (!activeRect) return;

            const delta = dragState.axis === 'y'
                ? event.offsetY - dragState.startY
                : event.offsetX - dragState.startX;
            const axisLength = dragState.axis === 'y' ? activeRect.height : activeRect.width;
            if (!Number.isFinite(axisLength) || axisLength <= 0) return;

            const scale = Math.exp((delta / axisLength) * axisZoomSpeed);
            const start = dragState.startRange?.start;
            const end = dragState.startRange?.end;
            if (!Number.isFinite(start) || !Number.isFinite(end)) return;

            const span = Math.max(end - start, 0.0001) * scale;
            const center = (start + end) / 2;
            const proposed = {
                start: center - span / 2,
                end: center + span / 2
            };

            const hardBounds = dragState.axis === 'y'
                ? { min: paddedYMin, max: paddedYMax }
                : {
                    min: numericPrices.length ? numericPrices[0] : 0,
                    max: numericPrices.length ? numericPrices[numericPrices.length - 1] : 0
                };

            const clamped = clampRange(proposed.start, proposed.end, hardBounds.min, hardBounds.max);
            applyAxisZoom(dragState.axis, clamped);
        };

        chart.getZr().on('mousedown', handleAxisDown);
        chart.getZr().on('pointerdown', handleAxisDown);
        chart.getZr().on('mousemove', handleAxisMove);
        chart.getZr().on('pointermove', handleAxisMove);

        const clearAxisDrag = () => {
            if (element.__payoffAxisDragState) {
                element.__payoffAxisDragState.isDragging = false;
            }
            element.__payoffIsInteracting = false;
            element.__payoffLastInteractionAt = Date.now();
        };

        chart.getZr().on('mouseup', clearAxisDrag);
        chart.getZr().on('pointerup', clearAxisDrag);
        chart.getZr().on('globalout', () => {
            element.__payoffIsTooltipVisible = false;
            setCursor('');
            clearAxisDrag();
        });

        if (!chart.isDisposed?.()) {
            chart.resize();
        }
    }
};

window.payoffChart.clearRanges = function (positionId) {
    if (!positionId) return;

    try {
        localStorage.removeItem(`payoffChart:${positionId}:priceRange`);
        localStorage.removeItem(`payoffChart:${positionId}:pnlRange`);
    } catch { /* ignore */ }
};

window.payoffChart.updateTempPrice = function (element, tempPrice) {
    if (!element || !element.__payoffInstance || element.__payoffInstance.isDisposed?.()) {
        return;
    }
    if (element.__payoffIsTooltipVisible) {
        return;
    }

    const chart = element.__payoffInstance;
    const price = Number(tempPrice);
    const formatPrice = element.__payoffFormatPrice || (value => String(value));
    const activeCollectionId = element.__payoffActiveCollectionId;

    const markLine = Number.isFinite(price)
        ? {
            silent: true,
            symbol: 'none',
            z: 3,
            lineStyle: { color: '#9E9E9E', width: 1.5, type: 'dashed' },
            label: {
                show: true,
                formatter: function () { return `Future: ${formatPrice(price)}`; },
                rotate: 90,
                position: 'insideEndTop',
                align: 'center',
                verticalAlign: 'top',
                distance: 10,
                fontSize: 9,
                color: '#CCCCCC'
            },
            data: [
                { xAxis: Number(price) }
            ]
        }
        : undefined;

    const seriesUpdate = [];
    if (activeCollectionId) {
        seriesUpdate.push({
            id: `${activeCollectionId}:expiry`,
            markLine: markLine
        });
    }

    chart.setOption({
        series: seriesUpdate
    }, {
        lazyUpdate: true,
        silent: true
    });

};

window.dailyPnlChart = {
    _instances: new Set(),
    _resizeTimer: null,
    _resizeScheduled: false,
    _ensureResizeListener: function () {
        if (window.dailyPnlChart._resizeScheduled) {
            return;
        }
        window.dailyPnlChart._resizeScheduled = true;
        const schedule = () => {
            if (window.dailyPnlChart._resizeTimer) {
                clearTimeout(window.dailyPnlChart._resizeTimer);
            }
            window.dailyPnlChart._resizeTimer = setTimeout(() => {
                window.dailyPnlChart._resizeTimer = null;
                window.dailyPnlChart._instances.forEach((element) => {
                    const instance = element && element.__dailyPnlInstance;
                    if (element && element.isConnected && instance && !instance.isDisposed?.()) {
                        instance.resize();
                    }
                });
            }, 80);
        };
        window.addEventListener('resize', schedule);
        window.addEventListener('orientationchange', schedule);
        if (window.matchMedia) {
            const media = window.matchMedia('(max-width: 600px)');
            const handler = () => schedule();
            if (media.addEventListener) {
                media.addEventListener('change', handler);
            } else if (media.addListener) {
                media.addListener(handler);
            }
        }
    },
    render: function (element, options) {
        if (!element || !options) return;

        if (!element.isConnected) {
            if (element.__dailyPnlInstance && !element.__dailyPnlInstance.isDisposed?.()) {
                element.__dailyPnlInstance.dispose();
            }
            element.__dailyPnlInstance = null;
            window.dailyPnlChart._instances.delete(element);
            if (element.__dailyPnlResizeObserver) {
                element.__dailyPnlResizeObserver.disconnect();
                element.__dailyPnlResizeObserver = null;
            }
            if (element.__dailyPnlResizeRaf) {
                cancelAnimationFrame(element.__dailyPnlResizeRaf);
                element.__dailyPnlResizeRaf = null;
            }
            return;
        }

        if (!element || typeof element.offsetWidth !== 'number' || typeof element.offsetHeight !== 'number') {
            return;
        }
        const hasSize = element.offsetWidth > 0 || element.offsetHeight > 0;
        if (!hasSize) {
            const attempts = (element.__dailyPnlRenderAttempts || 0) + 1;
            element.__dailyPnlRenderAttempts = attempts;
            if (attempts <= 5) {
                requestAnimationFrame(() => window.dailyPnlChart.render(element, options));
            }
            return;
        }

        element.__dailyPnlRenderAttempts = 0;

        if (element.__dailyPnlInstance?.isDisposed?.()) {
            element.__dailyPnlInstance = null;
        } else if (element.__dailyPnlInstance?.getDom?.() && element.__dailyPnlInstance.getDom() !== element) {
            element.__dailyPnlInstance.dispose();
            element.__dailyPnlInstance = null;
        }

        const chart = element.__dailyPnlInstance || echarts.init(element);
        element.__dailyPnlInstance = chart;
        window.dailyPnlChart._instances.add(element);
        window.dailyPnlChart._ensureResizeListener();
        const ensureResizeObserver = () => {
            if (element.__dailyPnlResizeObserver || typeof ResizeObserver !== 'function') {
                return;
            }
            const observer = new ResizeObserver(() => {
                if (!element.isConnected) {
                    observer.disconnect();
                    element.__dailyPnlResizeObserver = null;
                    return;
                }
                if (element.__dailyPnlResizeRaf) {
                    return;
                }
                element.__dailyPnlResizeRaf = requestAnimationFrame(() => {
                    element.__dailyPnlResizeRaf = null;
                    const instance = element.__dailyPnlInstance;
                    if (instance && !instance.isDisposed?.()) {
                        instance.resize();
                    }
                });
            });
            element.__dailyPnlResizeObserver = observer;
            observer.observe(element);
        };
        ensureResizeObserver();

        const labels = options.days || [];
        const collections = options.series || [];
        const yMin = Number.isFinite(options.yMin) ? Number(options.yMin) : 'dataMin';
        const yMax = Number.isFinite(options.yMax) ? Number(options.yMax) : 'dataMax';
        const palette = ['#26A69A', '#42A5F5', '#7E57C2', '#FFA726', '#66BB6A', '#EF5350', '#FFCA28', '#8D6E63'];

        const formatPnl = (value) => {
            const numeric = Number(value);
            if (!Number.isFinite(numeric)) return '0';
            const abs = Math.abs(numeric);
            const decimals = abs >= 100 ? 2 : abs >= 1 ? 2 : 4;
            return numeric.toFixed(decimals).replace(/\.?0+$/, '');
        };

        const isNarrow = element.offsetWidth <= 520;
        const axisFontSize = isNarrow ? 8 : 10;
        const rotateLabels = isNarrow && labels.length > 8;

        const series = collections.map((collection, index) => {
            const values = collection.values || [];
            const color = collection.color || palette[index % palette.length];
            return {
                name: collection.name || `Series ${index + 1}`,
                type: 'bar',
                barMaxWidth: 26,
                data: values.map(value => Number.isFinite(value) ? Number(value) : 0),
                itemStyle: { color }
            };
        });

        chart.setOption({
            grid: { left: 55, right: 18, top: collections.length > 1 ? 45 : 30, bottom: rotateLabels ? 55 : 40 },
            legend: collections.length > 1
                ? {
                    top: 0,
                    left: 10,
                    textStyle: { color: '#E0E0E0' }
                }
                : undefined,
            tooltip: {
                trigger: 'axis',
                axisPointer: { type: 'shadow' },
                formatter: function (params) {
                    if (!params || !params.length) return '';
                    const day = params[0]?.axisValue ?? '';
                    const lines = [`${day}`];
                    params.forEach(p => {
                        const value = Number(p?.value);
                        if (!Number.isFinite(value)) return;
                        lines.push(`${p.marker || ''} ${p.seriesName}: ${formatPnl(value)}`);
                    });
                    return lines.join('<br/>');
                }
            },
            xAxis: {
                type: 'category',
                data: labels,
                boundaryGap: true,
                axisLabel: {
                    color: '#B0BEC5',
                    fontSize: axisFontSize,
                    rotate: rotateLabels ? 45 : 0
                },
                axisLine: { lineStyle: { color: '#546E7A' } }
            },
            yAxis: {
                type: 'value',
                min: yMin,
                max: yMax,
                axisLabel: {
                    color: '#B0BEC5',
                    fontSize: axisFontSize,
                    formatter: function (value) {
                        return formatPnl(value);
                    }
                },
                splitLine: { lineStyle: { color: 'rgba(224, 224, 224, 0.1)' } }
            },
            series: series
        }, true);

        if (!chart.isDisposed?.()) {
            chart.resize();
        }
    },
    dispose: function (element) {
        if (!element) return;
        const chart = element.__dailyPnlInstance;
        if (chart && !chart.isDisposed?.()) {
            chart.dispose();
        }
        element.__dailyPnlInstance = null;
    }
};
