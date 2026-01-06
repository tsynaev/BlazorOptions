window.payoffChart = {
    render: function (element, options) {
        if (!element || !options) return;

        const chart = element.__payoffInstance || echarts.init(element);
        element.__payoffInstance = chart;

        const labels = options.labels || [];
        const profits = options.profits || [];
        const prices = (options.prices && options.prices.length ? options.prices : labels) || [];
        const yMin = options.yMin ?? 'dataMin';
        const yMax = options.yMax ?? 'dataMax';
        const positionKey = options.positionId || 'default';

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

        const pricePoints = numericPrices.map((price, index) => [price, profits[index]]);
        const positivePoints = pricePoints.map(([price, value]) => [price, value > 0 ? value : null]);
        const negativePoints = pricePoints.map(([price, value]) => [price, value < 0 ? value : null]);

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
            grid: { left: 60, right: 20, top: 20, bottom: 50 },
            toolbox: {
                feature: {
                    restore: {},
                    dataZoom: { yAxisIndex: false }
                },
                right: 10
            },
            tooltip: {
                trigger: 'axis',
                axisPointer: { type: 'line' },
                formatter: function (params) {
                    if (!params || !params.length) return '';
                    const validPoint = params.find(p => p && p.value && p.value.length === 2 && p.value[1] !== null && p.value[1] !== undefined && !Number.isNaN(p.value[1]));
                    if (!validPoint) return '';

                    const price = Number(validPoint.value[0]).toFixed(0);
                    const profit = Number(validPoint.value[1]).toFixed(2);

                    return `Price: ${price}<br/>P/L: ${profit}`;
                }
            },
            xAxis: {
                type: 'value',
                min: 'dataMin',
                max: 'dataMax',
                boundaryGap: ['2%', '2%'],
                axisLabel: {
                    formatter: function (value) {
                        return Number(value).toFixed(0);
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
                splitLine: { lineStyle: { color: '#e0e0e0' } }
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
                    }
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
                    name: 'Break-even',
                    type: 'scatter',
                    data: breakEvens.map(price => ({
                        value: [price, 0],
                        symbolSize: 10,
                        itemStyle: { color: '#607D8B' },
                        label: {
                            show: true,
                            formatter: function (params) { return Number(params.value[0]).toFixed(0); },
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

        chart.resize();
    }
};

window.payoffChart.clearRanges = function (positionId) {
    if (!positionId) return;

    try {
        localStorage.removeItem(`payoffChart:${positionId}:priceRange`);
        localStorage.removeItem(`payoffChart:${positionId}:pnlRange`);
    } catch { /* ignore */ }
};
