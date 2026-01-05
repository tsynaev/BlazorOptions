window.payoffChart = {
    render: function (element, options) {
        if (!element || !options) return;

        const chart = element.__payoffInstance || echarts.init(element);
        element.__payoffInstance = chart;

        const labels = options.labels || [];
        const profits = options.profits || [];
        const yMin = options.yMin ?? 'dataMin';
        const yMax = options.yMax ?? 'dataMax';

        chart.setOption({
            grid: { left: 60, right: 20, top: 20, bottom: 50 },
            tooltip: {
                trigger: 'axis',
                formatter: function (params) {
                    if (!params || !params.length) return '';
                    const point = params[0];
                    return `Price: ${labels[point.dataIndex]}<br/>P/L: ${point.value.toFixed(2)}`;
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
            series: [
                {
                    name: 'P/L at Expiry',
                    type: 'line',
                    smooth: true,
                    data: profits,
                    symbol: 'none',
                    lineStyle: {
                        color: '#4CAF50',
                        width: 3
                    },
                    areaStyle: {
                        color: 'rgba(76, 175, 80, 0.12)'
                    }
                }
            ]
        }, true);

        chart.resize();
    }
};
