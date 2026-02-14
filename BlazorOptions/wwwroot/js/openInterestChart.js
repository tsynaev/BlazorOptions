export function render(element, options) {
        if (!element || !options || typeof echarts === 'undefined') {
            return;
        }

        let chart = element.__openInterestChartInstance;
        if (!chart || chart.isDisposed?.()) {
            chart = echarts.init(element, null, { renderer: 'canvas', useDirtyRect: true });
            element.__openInterestChartInstance = chart;
            if (typeof ResizeObserver === 'function') {
                const observer = new ResizeObserver(() => {
                    const instance = element.__openInterestChartInstance;
                    if (instance && !instance.isDisposed?.()) {
                        instance.resize();
                    }
                });
                observer.observe(element);
                element.__openInterestChartResizeObserver = observer;
            }
        }

        const strikes = Array.isArray(options.strikes) ? options.strikes : [];
        const expirations = Array.isArray(options.expirations) ? options.expirations : [];
        const raw = Array.isArray(options.data) ? options.data : [];
        const max = Number.isFinite(options.maxValue) ? Number(options.maxValue) : 0;
        const data = raw.map(item => [Number(item.strikeIndex), Number(item.expirationIndex), Number(item.value)]);

        chart.setOption({
            backgroundColor: 'transparent',
            tooltip: {
                formatter: function (params) {
                    const v = params?.value || [];
                    const strike = strikes[v[0]] || v[0];
                    const expiration = expirations[v[1]] || v[1];
                    return `Exp: ${expiration}<br/>Strike: ${strike}<br/>OI: ${Number(v[2] || 0).toLocaleString()}`;
                }
            },
            visualMap: {
                max: Math.max(1, max),
                inRange: {
                    color: ['#1d4ed8', '#14b8a6', '#22c55e', '#facc15', '#ef4444']
                }
            },
            xAxis3D: { type: 'category', data: strikes, name: 'Strike' },
            yAxis3D: { type: 'category', data: expirations, name: 'Expiration' },
            zAxis3D: { type: 'value' },
            grid3D: {
                boxWidth: 160,
                boxDepth: 100,
                light: {
                    main: { intensity: 1.2 },
                    ambient: { intensity: 0.4 }
                },
                viewControl: {
                    projection: 'perspective',
                    alpha: 25,
                    beta: 45
                }
            },
            series: [
                {
                    type: 'bar3D',
                    data: data,
                    shading: 'lambert',
                    label: { show: false },
                    emphasis: {
                        label: { show: true, formatter: (p) => Number(p.value[2]).toFixed(0) }
                    }
                }
            ]
        }, true);
        chart.resize();
}

export function dispose(element) {
        if (!element) {
            return;
        }

        const observer = element.__openInterestChartResizeObserver;
        if (observer) {
            observer.disconnect();
            element.__openInterestChartResizeObserver = null;
        }

        const chart = element.__openInterestChartInstance;
        if (chart && !chart.isDisposed?.()) {
            chart.dispose();
        }
        element.__openInterestChartInstance = null;
}
