window.payoffChart = {
    render: function (element, options, dotNetHelper) {
        if (!element || !options) return;

        if (!element.isConnected) {
            if (element.__payoffInstance && !element.__payoffInstance.isDisposed?.()) {
                element.__payoffInstance.dispose();
            }
            element.__payoffInstance = null;
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

        const chart = element.__payoffInstance || echarts.init(element);
        element.__payoffInstance = chart;

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
        const axisPointerValue = Number.isFinite(tempPrice) ? Number(tempPrice) : undefined;

        const priceZoomInside = {
            type: 'inside',
            xAxisIndex: 0,
            filterMode: 'filter',
            zoomOnMouseWheel: true,
            moveOnMouseMove: true,
            startValue: priceRange?.start,
            endValue: priceRange?.end
        };

        const pnlZoomInside = {
            type: 'inside',
            yAxisIndex: 0,
            filterMode: 'none',
            zoomOnMouseWheel: true,
            moveOnMouseMove: true,
            zoomLock: false,
            startValue: pnlRange?.start ?? paddedYMin,
            endValue: pnlRange?.end ?? paddedYMax
        };

        const formatPrice = (value) => {
            const numeric = Number(value);
            if (!Number.isFinite(numeric)) return '';
            const abs = Math.abs(numeric);
            if (abs >= 100) {
                return Math.round(numeric).toString();
            }
            if (abs === 0) {
                return '0';
            }

            const magnitude = Math.floor(Math.log10(abs));
            const decimals = magnitude >= 0
                ? Math.max(0, 2 - magnitude)
                : Math.abs(magnitude) + 2;

            return numeric.toFixed(decimals);
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
                    value: axisPointerValue,
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
            element.__payoffLastZoomAt = Date.now();
            const zoomState = chart.getOption().dataZoom || [];
            const priceZoom = zoomState.find(z => z.xAxisIndex !== undefined);
            const pnlZoom = zoomState.find(z => z.yAxisIndex !== undefined);

            const firstLabel = numericPrices.length > 0 ? numericPrices[0] : 0;
            const lastLabel = numericPrices.length > 0 ? numericPrices[numericPrices.length - 1] : firstLabel;

            const defaultPriceRange = priceRange ?? { start: firstLabel, end: lastLabel };
            const defaultPnlRange = pnlRange ?? { start: paddedYMin, end: paddedYMax };

            const priceSelection = toRange(priceZoom, defaultPriceRange);
            const pnlSelection = toRange(pnlZoom, defaultPnlRange);

            if (priceSelection?.start !== undefined && priceSelection?.end !== undefined) {
                persistRange(priceRangeKey, priceSelection);
            }

            if (pnlSelection?.start !== undefined && pnlSelection?.end !== undefined) {
                persistRange(pnlRangeKey, pnlSelection);
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

        chart.off('click');
        chart.getZr().off('click');
        chart.getZr().off('mousedown');
        chart.getZr().off('mouseup');
        chart.getZr().off('globalout');
        if (element.__payoffDomClick) {
            element.removeEventListener('click', element.__payoffDomClick);
            element.__payoffDomClick = null;
        }

        if (dotNetHelper) {
            const invokeSelection = (price) => {
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
                const price = pickPriceFromPixels(event.offsetX, event.offsetY ?? 0);
                invokeSelection(price);
            });

        }

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
