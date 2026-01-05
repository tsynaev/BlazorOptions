window.payoffChart = {
    render: function (element, options) {
        if (!element || !options) return;

        const chart = element.__payoffInstance || echarts.init(element);
        element.__payoffInstance = chart;

        const labels = options.labels || [];
        const profits = options.profits || [];
        const yMin = options.yMin ?? 'dataMin';
        const yMax = options.yMax ?? 'dataMax';

        const positiveProfits = profits.map(v => (v > 0 ? v : null));
        const negativeProfits = profits.map(v => (v < 0 ? v : null));

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
                {
                    type: 'inside',
                    xAxisIndex: 0,
                    filterMode: 'filter',
                    zoomOnMouseWheel: true,
                    moveOnMouseMove: true
                }
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

        chart.resize();
    }
};
