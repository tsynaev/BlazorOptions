export function render(element, options) {
    if (!element || !options || typeof echarts === 'undefined') {
        return;
    }

    let chart = element.__volatilitySkewSurfaceInstance;
    if (!chart || chart.isDisposed?.()) {
        chart = echarts.init(element, null, { renderer: 'canvas', useDirtyRect: true });
        element.__volatilitySkewSurfaceInstance = chart;
        if (typeof ResizeObserver === 'function') {
            const observer = new ResizeObserver(() => {
                const instance = element.__volatilitySkewSurfaceInstance;
                if (instance && !instance.isDisposed?.()) {
                    instance.resize();
                }
            });
            observer.observe(element);
            element.__volatilitySkewSurfaceResizeObserver = observer;
        }
    }

    const strikes = Array.isArray(options.strikes) ? options.strikes : [];
    const expirations = Array.isArray(options.expirations) ? options.expirations : [];
    const raw = Array.isArray(options.data) ? options.data : [];
    const min = Number.isFinite(options.minIv) ? Number(options.minIv) : 0;
    const max = Number.isFinite(options.maxIv) ? Number(options.maxIv) : 0;
    const points = raw.map(p => [Number(p.strikeIndex), Number(p.expirationIndex), Number(p.ivPercent)]);

    chart.setOption({
        backgroundColor: 'transparent',
        tooltip: {
            formatter: params => {
                const value = params?.value || [0, 0, 0];
                const strike = strikes[value[0]] ?? value[0];
                const exp = expirations[value[1]] ?? value[1];
                return `Strike: ${Number(strike).toFixed(2)}<br/>Exp: ${exp}<br/>IV: ${Number(value[2]).toFixed(2)}%`;
            }
        },
        visualMap: {
            min,
            max: Math.max(min + 1, max),
            calculable: true,
            orient: 'horizontal',
            left: 'center',
            bottom: 10,
            inRange: {
                color: ['#1d4ed8', '#0ea5e9', '#22c55e', '#facc15', '#ef4444']
            },
            textStyle: { color: '#cbd5e1' }
        },
        xAxis3D: {
            type: 'category',
            name: 'Strike',
            data: strikes.map(v => Number(v).toFixed(0)),
            axisLabel: { color: '#cbd5e1', fontSize: 10 }
        },
        yAxis3D: {
            type: 'category',
            name: 'Exp Date',
            data: expirations,
            axisLabel: { color: '#cbd5e1', fontSize: 10 }
        },
        zAxis3D: {
            type: 'value',
            name: 'IV %',
            axisLabel: { color: '#cbd5e1', formatter: '{value}%' }
        },
        grid3D: {
            boxWidth: 180,
            boxDepth: 90,
            light: {
                main: { intensity: 1.2, shadow: true },
                ambient: { intensity: 0.35 }
            },
            viewControl: {
                projection: 'perspective',
                alpha: 25,
                beta: 35,
                distance: 220
            },
            axisLine: { lineStyle: { color: '#64748b' } },
            axisPointer: { lineStyle: { color: '#e2e8f0' } }
        },
        series: [
            {
                type: 'surface',
                data: points,
                shading: 'realistic',
                realisticMaterial: {
                    roughness: 0.2,
                    metalness: 0
                },
                wireframe: {
                    show: false
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

    const observer = element.__volatilitySkewSurfaceResizeObserver;
    if (observer) {
        observer.disconnect();
        element.__volatilitySkewSurfaceResizeObserver = null;
    }

    const chart = element.__volatilitySkewSurfaceInstance;
    if (chart && !chart.isDisposed?.()) {
        chart.dispose();
    }
    element.__volatilitySkewSurfaceInstance = null;
}
