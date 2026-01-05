window.payoffChart = {
    render: function (element, options) {
        if (!element || !options) return;

        const chart = element.__payoffInstance || echarts.init(element);
        element.__payoffInstance = chart;

        const labels = options.labels || [];
        const profits = options.profits || [];
        const yMin = options.yMin ?? 'dataMin';
        const yMax = options.yMax ?? 'dataMax';

        const priceRangeKey = 'payoffChart:lastPriceRange';
        const pnlRangeKey = 'payoffChart:lastPnlRange';
        const numericLabels = labels.map(label => Number(label));

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

        const positiveProfits = profits.map(v => (v > 0 ? v : null));
        const negativeProfits = profits.map(v => (v < 0 ? v : null));

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
            startValue: pnlRange?.start ?? yMin,
            endValue: pnlRange?.end ?? yMax
        };

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
                formatter: function (params) {
                    if (!params || !params.length) return '';
                    const validPoint = params.find(p => p && p.value !== null && p.value !== undefined && !Number.isNaN(p.value));
                    if (!validPoint) return '';

                    const price = labels[validPoint.dataIndex];
                    const profit = Number(validPoint.value).toFixed(2);

                    return `Price: ${price}<br/>P/L: ${profit}`;
                }
            },
            xAxis: {
                type: 'category',
                data: labels,
                boundaryGap: false,
                axisLabel: { interval: 'auto' },
                axisPointer: { snap: true }
            },
            yAxis: {
                type: 'value',
                min: yMin,
                max: yMax,
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
                    data: positiveProfits,
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
                    data: negativeProfits,
                    symbol: 'none',
                    lineStyle: {
                        color: '#F44336',
                        width: 3
                    },
                    areaStyle: {
                        color: 'rgba(244, 67, 54, 0.15)'
                    }
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

            const priceSelection = toRange(priceZoom, priceRange ?? { start: numericLabels.at(0), end: numericLabels.at(-1) });
            const pnlSelection = toRange(pnlZoom, pnlRange ?? { start: yMin, end: yMax });

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
