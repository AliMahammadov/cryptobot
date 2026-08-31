class CandlestickChart {
  constructor(canvasId) {
    this.canvas = document.getElementById(canvasId);
    this.ctx = this.canvas.getContext('2d');
    this.klines = [];
    this.resize();
    window.addEventListener('resize', () => this.resize());
  }

  resize() {
    const rect = this.canvas.parentElement.getBoundingClientRect();
    this.width = rect.width;
    this.height = rect.height;
    this.canvas.width = this.width * window.devicePixelRatio;
    this.canvas.height = this.height * window.devicePixelRatio;
    this.ctx.scale(window.devicePixelRatio, window.devicePixelRatio);
    this.render();
  }

  setData(klines) {
    this.klines = klines || [];
    this.render();
  }

  render() {
    const ctx = this.ctx;
    const w = this.width;
    const h = this.height;

    ctx.clearRect(0, 0, w, h);
    if (!this.klines || this.klines.length === 0) return;

    // Background Grid
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.04)';
    ctx.lineWidth = 1;
    for (let y = 30; y < h; y += 40) {
      ctx.beginPath();
      ctx.moveTo(0, y);
      ctx.lineTo(w, y);
      ctx.stroke();
    }

    const count = this.klines.length;
    const candleWidth = Math.max(3, (w - 60) / count);
    const spacing = candleWidth * 0.25;
    const actualBarWidth = candleWidth - spacing;

    let minPrice = Infinity;
    let maxPrice = -Infinity;
    let maxVol = 0;

    for (const k of this.klines) {
      if (k.low < minPrice) minPrice = k.low;
      if (k.high > maxPrice) maxPrice = k.high;
      if (k.volume > maxVol) maxVol = k.volume;
    }

    const priceMargin = (maxPrice - minPrice) * 0.1 || 1;
    minPrice -= priceMargin;
    maxPrice += priceMargin;
    const priceRange = maxPrice - minPrice;

    const chartHeight = h * 0.75;
    const volHeight = h * 0.20;

    const getY = (price) => chartHeight - ((price - minPrice) / priceRange) * chartHeight + 10;

    // Render Candles
    for (let i = 0; i < count; i++) {
      const k = this.klines[i];
      const x = i * candleWidth + 10;
      const isGreen = k.close >= k.open;

      const openY = getY(k.open);
      const closeY = getY(k.close);
      const highY = getY(k.high);
      const lowY = getY(k.low);

      const color = isGreen ? '#00e676' : '#ff1744';
      ctx.fillStyle = color;
      ctx.strokeStyle = color;

      // Wick
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.moveTo(x + actualBarWidth / 2, highY);
      ctx.lineTo(x + actualBarWidth / 2, lowY);
      ctx.stroke();

      // Body
      const bodyTop = Math.min(openY, closeY);
      const bodyHeight = Math.max(2, Math.abs(closeY - openY));
      ctx.fillRect(x, bodyTop, actualBarWidth, bodyHeight);

      // Volume Bar at bottom
      const vH = (k.volume / maxVol) * volHeight;
      ctx.fillStyle = isGreen ? 'rgba(0, 230, 118, 0.25)' : 'rgba(255, 23, 68, 0.25)';
      ctx.fillRect(x, h - vH, actualBarWidth, vH);
    }

    // Price scale on right
    ctx.fillStyle = '#8a99ad';
    ctx.font = '10px JetBrains Mono, monospace';
    ctx.textAlign = 'right';
    const lastPrice = this.klines[count - 1].close;
    const lastY = getY(lastPrice);

    ctx.fillStyle = 'rgba(0, 240, 255, 0.2)';
    ctx.fillRect(w - 55, lastY - 9, 52, 18);
    ctx.fillStyle = '#00f0ff';
    ctx.fillText(lastPrice.toFixed(2), w - 8, lastY + 3);
  }
}
window.CandlestickChart = CandlestickChart;
