const instances = new WeakMap();

function ensureInstance(element, isDarkTheme) {
    if (!element) {
        return null;
    }

    const existing = instances.get(element);
    if (existing && !existing.isDisposed()) {
        return existing;
    }

    const instance = echarts.init(element, isDarkTheme ? "dark" : null);
    instances.set(element, instance);
    return instance;
}

export function render(element, labels, candles, averageValue, isDarkTheme) {
    const chart = ensureInstance(element, isDarkTheme);
    if (!chart) {
        return;
    }
    const option = {
        animation: false,
        grid: {
            left: 16,
            right: 52,
            top: 20,
            bottom: 42,
            containLabel: true
        },
        tooltip: {
            trigger: "axis",
            axisPointer: {
                type: "cross",
                label: {
                    backgroundColor: "#334155"
                }
            },
            formatter: function (params) {
                if (!params || params.length === 0) {
                    return "";
                }
                const first = params[0];
                const date = first.axisValueLabel || first.axisValue || "";
                const candle = first.data;
                if (!Array.isArray(candle) || candle.length < 4) {
                    return date;
                }

                const open = Number(candle[0]).toFixed(2);
                const close = Number(candle[1]).toFixed(2);
                const low = Number(candle[2]).toFixed(2);
                const high = Number(candle[3]).toFixed(2);
                return `${date}<br/>O: ${open} C: ${close}<br/>L: ${low} H: ${high}`;
            }
        },
        xAxis: {
            type: "category",
            data: labels,
            boundaryGap: false,
            axisPointer: {
                show: true,
                label: {
                    show: true
                }
            },
            axisLabel: {
                color: "#94a3b8",
                fontSize: 10,
                interval: 0,
                formatter: function (value) {
                    if (!value) {
                        return "";
                    }

                    // Label density similar to trading platforms:
                    // month starts as "Oct", mid-month as "16", year boundary as "2026".
                    const parts = String(value).split("-");
                    if (parts.length < 3) {
                        return value;
                    }

                    const year = Number(parts[0]);
                    const month = Number(parts[1]);
                    const day = Number(parts[2]);
                    if (!Number.isFinite(year) || !Number.isFinite(month) || !Number.isFinite(day)) {
                        return value;
                    }

                    if (day === 1) {
                        if (month === 1) {
                            return String(year);
                        }

                        const monthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
                        return monthNames[month - 1] ?? "";
                    }

                    if (day === 16) {
                        return "16";
                    }

                    return "";
                }
            },
            axisLine: {
                lineStyle: {
                    color: "#334155"
                }
            }
        },
        yAxis: {
            type: "value",
            position: "right",
            axisLabel: {
                color: "#94a3b8",
                formatter: "{value}%"
            },
            splitLine: {
                lineStyle: {
                    color: "rgba(148, 163, 184, 0.18)"
                }
            }
        },
        series: [
            {
                type: "candlestick",
                data: candles,
                itemStyle: {
                    color: "#2EBD85",
                    color0: "#EF4444",
                    borderColor: "#2EBD85",
                    borderColor0: "#EF4444"
                },
                markLine: averageValue == null
                    ? undefined
                    : {
                        symbol: "none",
                        label: {
                            show: true,
                            formatter: `Avg 1Y: ${Number(averageValue).toFixed(2)}%`,
                            color: "#94a3b8"
                        },
                        lineStyle: {
                            color: "#64748b",
                            width: 1,
                            type: "dashed"
                        },
                        data: [{ yAxis: averageValue }]
                    }
            }
        ]
    };

    chart.setOption(option, true);
    chart.resize();
}

export function dispose(element) {
    if (!element) {
        return;
    }

    const chart = instances.get(element);
    if (chart && !chart.isDisposed()) {
        chart.dispose();
    }
    instances.delete(element);
}
