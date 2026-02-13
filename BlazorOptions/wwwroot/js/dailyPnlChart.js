window.dailyPnlChart = {
    render: function (element, options) {
        if (!element || !options || typeof echarts === 'undefined') {
            return;
        }

        let chart = element.__dailyPnlInstance;
        if (!chart || chart.isDisposed?.()) {
            chart = echarts.init(element, null, { renderer: 'canvas', useDirtyRect: true });
            element.__dailyPnlInstance = chart;

            if (typeof ResizeObserver === 'function') {
                const observer = new ResizeObserver(() => {
                    const instance = element.__dailyPnlInstance;
                    if (instance && !instance.isDisposed?.()) {
                        instance.resize();
                    }
                });
                observer.observe(element);
                element.__dailyPnlResizeObserver = observer;
            }
        }

        const labels = Array.isArray(options.days) ? options.days : [];
        const collections = Array.isArray(options.series) ? options.series : [];
        const yMin = Number.isFinite(options.yMin) ? Number(options.yMin) : 'dataMin';
        const yMax = Number.isFinite(options.yMax) ? Number(options.yMax) : 'dataMax';
        const palette = ['#26A69A', '#42A5F5', '#7E57C2', '#FFA726', '#66BB6A', '#EF5350', '#FFCA28', '#8D6E63'];

        const formatPnl = (value) => {
            const numeric = Number(value);
            if (!Number.isFinite(numeric)) {
                return '0';
            }

            const abs = Math.abs(numeric);
            const decimals = abs >= 100 ? 2 : abs >= 1 ? 2 : 4;
            return numeric.toFixed(decimals).replace(/\.?0+$/, '');
        };

        const series = collections.map((collection, index) => {
            const values = Array.isArray(collection?.values) ? collection.values : [];
            const color = collection?.color || palette[index % palette.length];
            return {
                name: collection?.name || `Series ${index + 1}`,
                type: 'bar',
                barMaxWidth: 26,
                data: values.map(value => Number.isFinite(value) ? Number(value) : 0),
                itemStyle: { color }
            };
        });

        chart.setOption({
            grid: { left: 55, right: 18, top: collections.length > 1 ? 45 : 30, bottom: 42 },
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
                    if (!params || !params.length) {
                        return '';
                    }

                    const day = params[0]?.axisValue ?? '';
                    const lines = [`${day}`];
                    params.forEach(p => {
                        const value = Number(p?.value);
                        if (Number.isFinite(value)) {
                            lines.push(`${p.marker || ''} ${p.seriesName}: ${formatPnl(value)}`);
                        }
                    });
                    return lines.join('<br/>');
                }
            },
            xAxis: {
                type: 'category',
                data: labels,
                boundaryGap: true,
                axisLabel: { color: '#B0BEC5', fontSize: 10 },
                axisLine: { lineStyle: { color: '#546E7A' } }
            },
            yAxis: {
                type: 'value',
                min: yMin,
                max: yMax,
                axisLabel: {
                    color: '#B0BEC5',
                    fontSize: 10,
                    formatter: function (value) {
                        return formatPnl(value);
                    }
                },
                splitLine: { lineStyle: { color: 'rgba(224, 224, 224, 0.1)' } }
            },
            series
        }, true);

        chart.resize();
    },

    dispose: function (element) {
        if (!element) {
            return;
        }

        const observer = element.__dailyPnlResizeObserver;
        if (observer) {
            observer.disconnect();
            element.__dailyPnlResizeObserver = null;
        }

        const chart = element.__dailyPnlInstance;
        if (chart && !chart.isDisposed?.()) {
            chart.dispose();
        }

        element.__dailyPnlInstance = null;
    }
};
