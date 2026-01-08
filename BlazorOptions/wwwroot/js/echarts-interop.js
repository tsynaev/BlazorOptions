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
        const profits = options.profits || [];
        const theoreticalProfits = options.theoreticalProfits || [];
        const prices = (options.prices && options.prices.length ? options.prices : labels) || [];
        const yMin = options.yMin ?? 'dataMin';
        const yMax = options.yMax ?? 'dataMax';
        const positionKey = options.positionId || 'default';
        const tempPrice = options.temporaryPrice;
        const tempPnl = options.temporaryPnl;
        const tempExpiryPnl = options.temporaryExpiryPnl;

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

        const pricePoints = numericPrices.map((price, index) => [price, profits[index]]);
        const positivePoints = pricePoints.map(([price, value]) => [price, value > 0 ? value : null]);
        const negativePoints = pricePoints.map(([price, value]) => [price, value < 0 ? value : null]);
        const theoreticalPoints = numericPrices.map((price, index) => [price, theoreticalProfits[index]]);
        const tempPoint = Number.isFinite(tempPrice) && Number.isFinite(tempPnl)
            ? [{ value: [Number(tempPrice), Number(tempPnl)], symbolSize: 9 }]
            : [];
        const tempExpiryPoint = Number.isFinite(tempPrice) && Number.isFinite(tempExpiryPnl)
            ? [{ value: [Number(tempPrice), Number(tempExpiryPnl)], symbolSize: 9 }]
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
                    distance: 16,
                    fontSize: 11,
                    color: '#455A64',
                    textBorderColor: '#FFFFFF',
                    textBorderWidth: 3
                },
                data: [
                    { xAxis: Number(tempPrice) }
                ]
            }
            : null;

        const breakEvens = [];
        for (let i = 1; i < pricePoints.length; i++) {
            const [prevPrice, prevProfit] = pricePoints[i - 1];
            const [currPrice, currProfit] = pricePoints[i];

            if (prevProfit === undefined || currProfit === undefined) continue;
            if ((prevProfit <= 0 && currProfit >= 0) || (prevProfit >= 0 && currProfit <= 0)) {
                const deltaProfit = currProfit - prevProfit;
                const ratio = deltaProfit === 0 ? 0 : -prevProfit / deltaProfit;
                const price = prevPrice + (currPrice - prevPrice) * ratio;

                if (Number.isFinite(price)) {
                    breakEvens.push(price);
                }
            }
        }

        chart.setOption({
            grid: { left: 60, right: 20, top: 30, bottom: 50 },
            toolbox: {
                feature: {
                    restore: {},
                    dataZoom: { yAxisIndex: false }
                },
                right: 10
            },
            tooltip: {
                trigger: 'axis',
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

                    const valuesBySeries = {};
                    params.forEach(p => {
                        if (!p || !p.seriesName) return;
                        const value = Array.isArray(p.value) ? p.value[1] : (p.data && p.data.value ? p.data.value[1] : undefined);
                        if (value === null || value === undefined || Number.isNaN(value)) return;
                        valuesBySeries[p.seriesName] = value;
                    });

                    const lines = [`Price: ${formatPrice(price)}`];
                    Object.entries(valuesBySeries).forEach(([series, value]) => {
                        lines.push(`${series}: ${Number(value).toFixed(2)}`);
                    });

                    return lines.join('<br/>');
                }
            },
            xAxis: {
                type: 'value',
                min: 'dataMin',
                max: 'dataMax',
                boundaryGap: ['2%', '2%'],
                axisLabel: {
                    formatter: function (value) {
                        return formatPrice(value);
                    }
                },
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
                boundaryGap: ['20%', '20%'],
                scale: true,
                axisLabel: {
                    formatter: function (value) {
                        return value.toFixed(0);
                    }
                },
                splitLine: { lineStyle: { color: '#e0e0e0' } },
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
            series: [
                {
                    name: 'P/L (Profit)',
                    type: 'line',
                    smooth: true,
                    data: positivePoints,
                    symbol: 'none',
                    lineStyle: {
                        color: '#4CAF50',
                        width: 3
                    },
                    areaStyle: {
                        color: 'rgba(76, 175, 80, 0.12)'
                    },
                    markLine: indexMarkLine || undefined
                },
                {
                    name: 'P/L (Loss)',
                    type: 'line',
                    smooth: true,
                    data: negativePoints,
                    symbol: 'none',
                    lineStyle: {
                        color: '#F44336',
                        width: 3
                    },
                    areaStyle: {
                        color: 'rgba(244, 67, 54, 0.15)'
                    }
                },
                {
                    name: 'Temp P/L (Black-Scholes)',
                    type: 'line',
                    smooth: true,
                    data: theoreticalPoints,
                    symbol: 'none',
                    lineStyle: {
                        color: '#2196F3',
                        width: 2,
                        type: 'dashed'
                    }
                },
                {
                    name: 'Temp P/L',
                    type: 'scatter',
                    data: tempPoint,
                    symbolSize: 9,
                    itemStyle: { color: '#1976D2' },
                    label: {
                        show: tempPoint.length > 0,
                        formatter: function (params) {
                            if (!params || !params.value || params.value.length < 2) return '';
                            return `${Number(params.value[1]).toFixed(2)}`;
                        },
                        position: 'right',
                        offset: [8, -6],
                        fontSize: 10,
                        color: '#0D47A1',
                        textBorderColor: '#E3F2FD',
                        textBorderWidth: 3
                    }
                },
                {
                    name: 'Expiry P/L',
                    type: 'scatter',
                    data: tempExpiryPoint,
                    symbolSize: 9,
                    itemStyle: { color: '#8E24AA' },
                    label: {
                        show: tempExpiryPoint.length > 0,
                        formatter: function (params) {
                            if (!params || !params.value || params.value.length < 2) return '';
                            return `${Number(params.value[1]).toFixed(2)}`;
                        },
                        position: 'right',
                        offset: [8, -6],
                        fontSize: 10,
                        color: '#4A148C',
                        textBorderColor: '#F3E5F5',
                        textBorderWidth: 3
                    }
                },
                {
                    name: 'Break-even',
                    type: 'scatter',
                    data: breakEvens.map(price => ({
                        value: [price, 0],
                        symbolSize: 10,
                        itemStyle: { color: '#607D8B' },
                        label: {
                            show: true,
                            formatter: function (params) { return formatPrice(params.value[0]); },
                            position: 'top',
                            fontSize: 10,
                            color: '#37474F',
                            padding: [4, 6, 2, 6]
                        }
                    })),
                    tooltip: { show: false }
                }
            ]
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
