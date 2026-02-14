export function render(element, options) {
    if (!element || !options || typeof echarts === 'undefined') {
        return;
    }

    let chart = element.__volatilitySkewChartInstance;
    if (!chart || chart.isDisposed?.()) {
        chart = echarts.init(element, null, { renderer: 'canvas', useDirtyRect: true });
        element.__volatilitySkewChartInstance = chart;
        if (typeof ResizeObserver === 'function') {
            const observer = new ResizeObserver(() => {
                const instance = element.__volatilitySkewChartInstance;
                if (instance && !instance.isDisposed?.()) {
                    instance.resize();
                }
            });
            observer.observe(element);
            element.__volatilitySkewChartResizeObserver = observer;
        }
    }

    const palette = ['#38bdf8', '#22c55e', '#f59e0b', '#f97316', '#ec4899', '#a78bfa', '#ef4444'];
    const sourceSeries = Array.isArray(options.series) ? options.series : [];
    const showBidAskMarkers = !!options.showBidAskMarkers;
    const chartSeries = [];
    const strikeSet = new Set();

    for (let i = 0; i < sourceSeries.length; i++) {
        const item = sourceSeries[i] || {};
        const points = Array.isArray(item.points) ? item.points : [];
        for (const p of points) {
            const strike = Number(p.strike);
            if (Number.isFinite(strike) && strike > 0) {
                strikeSet.add(strike);
            }
        }
    }

    const strikeValues = Array.from(strikeSet).sort((a, b) => a - b);
    const validStrikes = strikeValues.filter(v => Number.isFinite(v) && v > 0);
    const buildLineValues = (points, selector) => {
        const byStrike = new Map();
        for (const p of points) {
            byStrike.set(Number(p.strike), Number(selector(p)));
        }

        const raw = strikeValues.map(strike => {
            const value = byStrike.get(strike);
            return Number.isFinite(value) ? value : null;
        });

        return interpolateMissing(raw);
    };

    const buildLinePoints = (values) => {
        const data = [];
        for (let i = 0; i < strikeValues.length; i++) {
            data.push([strikeValues[i], values[i]]);
        }

        return data;
    };

    const interpolateMissing = (values) => {
        const result = values.slice();
        const known = [];
        for (let i = 0; i < result.length; i++) {
            if (result[i] !== null) {
                known.push(i);
            }
        }

        if (known.length === 0) {
            return result;
        }

        for (let k = 0; k < known.length - 1; k++) {
            const leftIndex = known[k];
            const rightIndex = known[k + 1];
            const leftValue = result[leftIndex];
            const rightValue = result[rightIndex];
            const gap = rightIndex - leftIndex;
            if (gap <= 1) {
                continue;
            }

            for (let i = leftIndex + 1; i < rightIndex; i++) {
                const t = (i - leftIndex) / gap;
                result[i] = leftValue + (rightValue - leftValue) * t;
            }
        }

        return result;
    };

    for (let i = 0; i < sourceSeries.length; i++) {
        const item = sourceSeries[i] || {};
        const points = Array.isArray(item.points) ? item.points : [];
        const color = item.colorHex || palette[i % palette.length];

        const markIvValues = buildLineValues(points, p => p.markIv);
        const markPriceValues = buildLineValues(points, p => p.markPrice);

        chartSeries.push({
            name: `${item.name || `Exp ${i + 1}`} Mark`,
            type: 'line',
            showSymbol: false,
            smooth: false,
            connectNulls: true,
            lineStyle: { width: 2, color },
            itemStyle: { color },
            data: buildLinePoints(markIvValues).map((point, idx) => ({
                value: point,
                markIv: markIvValues[idx],
                markPrice: markPriceValues[idx]
            }))
        });

        if (showBidAskMarkers) {
            chartSeries.push({
                name: `${item.name || `Exp ${i + 1}`} Bid`,
                type: 'scatter',
                symbol: 'triangle',
                symbolSize: 8,
                itemStyle: { color: '#22c55e' },
                data: points
                    .filter(p => Number(p.bidIv) > 0)
                    .map(p => ({
                        value: [Number(p.strike), Number(p.bidIv)],
                        bidIv: Number(p.bidIv),
                        bidPrice: Number(p.bidPrice)
                    }))
            });

            chartSeries.push({
                name: `${item.name || `Exp ${i + 1}`} Ask`,
                type: 'scatter',
                symbol: 'triangle',
                symbolRotate: 180,
                symbolSize: 8,
                itemStyle: { color: '#ef4444' },
                data: points
                    .filter(p => Number(p.askIv) > 0)
                    .map(p => ({
                        value: [Number(p.strike), Number(p.askIv)],
                        askIv: Number(p.askIv),
                        askPrice: Number(p.askPrice)
                    }))
            });
        }
    }

    const currentPrice = Number(options.currentPrice);
    if (Number.isFinite(currentPrice) && currentPrice > 0) {
        chartSeries.push({
            name: 'Current price',
            type: 'line',
            data: [],
            silent: true,
            markLine: {
                symbol: 'none',
                label: {
                    show: true,
                    formatter: `Price ${currentPrice.toFixed(2)}`,
                    color: '#e2e8f0',
                    position: 'insideEndTop'
                },
                lineStyle: {
                    color: '#f8fafc',
                    width: 2,
                    type: 'solid'
                },
                data: [{ xAxis: currentPrice }]
            }
        });
    }

    let xAxisMin;
    let xAxisMax;
    if (validStrikes.length > 0) {
        let minStrike = validStrikes[0];
        let maxStrike = validStrikes[validStrikes.length - 1];

        if (Number.isFinite(currentPrice) && currentPrice > 0) {
            minStrike = Math.min(minStrike, currentPrice);
            maxStrike = Math.max(maxStrike, currentPrice);
        }

        const range = Math.max(1, maxStrike - minStrike);
        const padding = range * 0.15;
        xAxisMin = Math.max(0, minStrike - padding);
        xAxisMax = maxStrike + padding;
    }

    chart.setOption({
        backgroundColor: 'transparent',
        animation: false,
        tooltip: {
            trigger: 'axis',
            axisPointer: { type: 'cross' },
            formatter: (params) => {
                const rows = Array.isArray(params) ? params : [params];
                if (rows.length === 0) {
                    return '';
                }

                const header = `${rows[0].axisValueLabel ?? ''}`;
                const lines = [header];

                for (const row of rows) {
                    if (!row || !row.seriesName) {
                        continue;
                    }

                    if (row.seriesName.endsWith(' Mark')) {
                        const iv = Number(row?.data?.markIv ?? row?.value ?? 0);
                        const price = Number(row?.data?.markPrice ?? 0);
                        lines.push(`${row.marker}${row.seriesName}: ${price.toFixed(2)} (IV: ${iv.toFixed(2)}%)`);
                        continue;
                    }

                    if (row.seriesName === 'Current price') {
                        continue;
                    }

                    if (row.seriesName.endsWith(' Bid')) {
                        const iv = Number(row?.data?.bidIv ?? (Array.isArray(row.value) ? row.value[1] : row.value) ?? 0);
                        const price = Number(row?.data?.bidPrice ?? 0);
                        lines.push(`${row.marker}${row.seriesName}: ${price.toFixed(2)} (IV: ${iv.toFixed(2)}%)`);
                        continue;
                    }

                    if (row.seriesName.endsWith(' Ask')) {
                        const iv = Number(row?.data?.askIv ?? (Array.isArray(row.value) ? row.value[1] : row.value) ?? 0);
                        const price = Number(row?.data?.askPrice ?? 0);
                        lines.push(`${row.marker}${row.seriesName}: ${price.toFixed(2)} (IV: ${iv.toFixed(2)}%)`);
                        continue;
                    }

                    const value = Array.isArray(row.value) ? Number(row.value[1]) : Number(row.value);
                    lines.push(`${row.marker}${row.seriesName}: ${value.toFixed(2)}%`);
                }

                return lines.join('<br/>');
            }
        },
        legend: {
            show: false
        },
        grid: {
            left: 56,
            right: 24,
            top: 48,
            bottom: 44
        },
        xAxis: {
            type: 'value',
            name: 'Strike',
            nameGap: 28,
            min: xAxisMin,
            max: xAxisMax,
            axisLabel: { show: true, color: '#cbd5e1', margin: 14 },
            axisLine: { lineStyle: { color: '#475569' } },
            splitLine: { lineStyle: { color: 'rgba(148,163,184,0.15)' } }
        },
        yAxis: {
            type: 'value',
            name: 'IV %',
            axisLabel: { color: '#cbd5e1', formatter: '{value}%' },
            axisLine: { lineStyle: { color: '#475569' } },
            splitLine: { lineStyle: { color: 'rgba(148,163,184,0.15)' } }
        },
        series: chartSeries
    }, true);

    chart.resize();
}

export function dispose(element) {
    if (!element) {
        return;
    }

    const observer = element.__volatilitySkewChartResizeObserver;
    if (observer) {
        observer.disconnect();
        element.__volatilitySkewChartResizeObserver = null;
    }

    const chart = element.__volatilitySkewChartInstance;
    if (chart && !chart.isDisposed?.()) {
        chart.dispose();
    }
    element.__volatilitySkewChartInstance = null;
}
