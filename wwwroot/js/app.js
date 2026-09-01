if ('serviceWorker' in navigator) {
  navigator.serviceWorker.getRegistrations().then(regs => {
    for (let r of regs) r.unregister();
  });
}
if ('caches' in window) {
  caches.keys().then(names => {
    for (let name of names) caches.delete(name);
  });
}

document.addEventListener('DOMContentLoaded', () => {
  let currentSymbol = 'SOLUSDT';
  let currentTimeframe = '15m';
  let chart = new CandlestickChart('mainChart');
  let token = localStorage.getItem('cs_user_token');
  let userRole = localStorage.getItem('cs_user_role') || 'USER';
  let allTickers = [];
  let allSignals = [];
  let activeSignalFilter = 'all';
  let ws = null;

  const loginOverlay = document.getElementById('loginOverlay');
  const loginUsername = document.getElementById('loginUsername');
  const loginPassword = document.getElementById('loginPassword');
  const btnLogin = document.getElementById('btnLogin');
  const btnLogout = document.getElementById('btnLogout');
  const marketSearchInput = document.getElementById('marketSearchInput');
  const chartCoinSelect = document.getElementById('chartCoinSelect');
  const btnCreateUser = document.getElementById('btnCreateUser');
  const adminNavTab = document.querySelector('[data-tab="tabSettings"]');

  // Check Login
  if (token) {
    loginOverlay.style.display = 'none';
    initializeApp();
  }

  btnLogin.addEventListener('click', async () => {
    const username = loginUsername.value.trim();
    const password = loginPassword.value.trim();
    if (!username || !password) {
      alert('Zehamet olmasa istifadeci adi ve parolu daxil edin!');
      return;
    }
    try {
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password })
      });
      if (res.ok) {
        const data = await res.json();
        localStorage.setItem('cs_user_token', data.token);
        localStorage.setItem('cs_username', data.username);
        localStorage.setItem('cs_user_role', data.role);
        userRole = data.role;
        loginOverlay.style.display = 'none';
        initializeApp();
      } else {
        alert('GiriÅŸ uÄŸursuz oldu! Ä°stifadÉ™Ã§i adÄ± vÉ™ ya parol yalnÄ±ÅŸdÄ±r.');
      }
    } catch (e) {
      alert('QoÅŸulma xÉ™tasÄ±: ' + e.message);
    }
  });

  loginPassword.addEventListener('keypress', (e) => {
    if (e.key === 'Enter') btnLogin.click();
  });

  if (btnLogout) {
    btnLogout.addEventListener('click', () => {
      localStorage.removeItem('cs_user_token');
      localStorage.removeItem('cs_username');
      localStorage.removeItem('cs_user_role');
      location.reload();
    });
  }

  // TAB SWITCHING
  document.querySelectorAll('.nav-item').forEach(item => {
    item.addEventListener('click', () => {
      document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
      document.querySelectorAll('.tab-pane').forEach(p => p.classList.remove('active'));

      item.classList.add('active');
      const targetTabId = item.getAttribute('data-tab');
      const targetPane = document.getElementById(targetTabId);
      if (targetPane) {
        targetPane.classList.add('active');
      }

      if (targetTabId === 'tabChart') {
        setTimeout(() => chart.resize(), 100);
      }
      if (targetTabId === 'tabSignals') {
        loadAllSignals();
      }
      if (targetTabId === 'tabNews') {
        loadNews();
      }
      if (targetTabId === 'tabSettings') {
        loadAdminUsers();
      }
    });
  });

  function initializeApp() {
    // RBAC: HIDE ADMIN TAB IF NOT SUPERADMIN
    if (adminNavTab) {
      if (userRole === 'SUPERADMIN') {
        adminNavTab.style.display = 'flex';
      } else {
        adminNavTab.style.display = 'none';
      }
    }

    loadBtcCompass();
    loadTickers();
    loadCoinAnalysis(currentSymbol, currentTimeframe);
    loadAllSignals();
    loadNews();
    if (userRole === 'SUPERADMIN') {
      loadAdminUsers();
    }
    initBinanceWebSocket();

    setInterval(() => {
      loadBtcCompass();
    }, 10000);
  }

  // REAL-TIME BINANCE WEBSOCKET FOR ALL 35+ COINS
  function initBinanceWebSocket() {
    try {
      if (ws) ws.close();
      ws = new WebSocket('wss://fstream.binance.com/ws/!ticker@arr');
      
      ws.onmessage = (event) => {
        const data = JSON.parse(event.data);
        if (!Array.isArray(data)) return;

        data.forEach(t => {
          const sym = t.s;
          const price = parseFloat(t.c);
          const change = parseFloat(t.P);

          if (sym === currentSymbol) {
            const activePriceEl = document.getElementById('activeChartPrice');
            if (activePriceEl) {
              const prev = parseFloat(activePriceEl.getAttribute('data-raw') || '0');
              activePriceEl.textContent = '$' + price.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
              activePriceEl.setAttribute('data-raw', price);
              if (prev > 0 && price !== prev) {
                activePriceEl.style.color = price > prev ? '#00e676' : '#ff3366';
                setTimeout(() => { activePriceEl.style.color = '#fff'; }, 300);
              }
            }
          }

          const existing = allTickers.find(x => x.symbol === sym);
          if (existing) {
            existing.price = price;
            existing.priceChangePercent = change;

            const rowPriceEl = document.getElementById('price_' + sym);
            const rowChangeEl = document.getElementById('change_' + sym);
            if (rowPriceEl) {
              rowPriceEl.textContent = '$' + price.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
            }
            if (rowChangeEl) {
              rowChangeEl.textContent = (change >= 0 ? '+' : '') + change.toFixed(2) + '%';
              rowChangeEl.style.color = change >= 0 ? '#00e676' : '#ff3366';
            }
          }
        });
      };

      ws.onclose = () => {
        setTimeout(initBinanceWebSocket, 3000);
      };
    } catch (e) {
      console.log('WS init error', e);
    }
  }

  // BTC Compass
  async function loadBtcCompass() {
    try {
      const res = await fetch('/api/btc-compass');
      if (!res.ok) return;
      const data = await res.json();
      
      document.getElementById('btcPrice').textContent = '$' + Number(data.price).toLocaleString('en-US', { minimumFractionDigits: 2 });
      const badge = document.getElementById('btcTrendBadge');
      badge.textContent = data.trend;
      badge.className = 'trend-badge ' + (data.trend.includes('YUKSELIS') || data.trend.includes('BULLISH') ? 'trend-bullish' : (data.trend.includes('ENIS') || data.trend.includes('BEARISH') ? 'trend-bearish' : 'trend-neutral'));
      document.getElementById('btcCompassBar').style.width = data.bullishScore + '%';
      document.getElementById('btcSummary').textContent = data.summary;
    } catch (e) {
      console.error('BTC Compass error', e);
    }
  }

  // Load 35+ Tickers
  async function loadTickers(isBackground = false) {
    try {
      const res = await fetch('/api/tickers');
      if (!res.ok) return;
      allTickers = await res.json();
      document.getElementById('totalCoinsCount').textContent = allTickers.length;
      
      if (!isBackground) {
        populateChartCoinSelect(allTickers);
      }
      renderMarketList(allTickers);
    } catch (e) {
      console.error('Tickers error', e);
    }
  }

  function populateChartCoinSelect(tickers) {
    chartCoinSelect.innerHTML = '';
    tickers.forEach(t => {
      const opt = document.createElement('option');
      opt.value = t.symbol;
      opt.textContent = t.symbol.replace('USDT', '') + ' / USDT Futures';
      if (t.symbol === currentSymbol) opt.selected = true;
      chartCoinSelect.appendChild(opt);
    });
  }

  function renderMarketList(tickers) {
    const container = document.getElementById('marketCoinList');
    const query = (marketSearchInput.value || '').trim().toUpperCase();
    const filtered = tickers.filter(t => t.symbol.includes(query));

    container.innerHTML = '';
    if (filtered.length === 0) {
      container.innerHTML = '<div style="text-align:center; padding:20px; color:var(--text-muted);">Coin tapilmadi</div>';
      return;
    }

    filtered.forEach(t => {
      const isUp = t.priceChangePercent >= 0;
      const cleanName = t.symbol.replace('USDT', '');
      const card = document.createElement('div');
      card.className = 'coin-row-card';
      card.innerHTML = `
        <div class="coin-row-info">
          <div class="coin-sym-icon">${cleanName.substring(0, 3)}</div>
          <div>
            <div class="coin-row-name">${cleanName} <span style="font-size:10px; color:var(--text-muted);">USDT</span></div>
            <div class="coin-row-sub">Hecm: $${(t.volumeQuote / 1000000).toFixed(1)}M</div>
          </div>
        </div>
        <div class="coin-row-stat">
          <div id="price_${t.symbol}" class="coin-row-price">$${Number(t.price).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 })}</div>
          <div id="change_${t.symbol}" class="coin-row-change" style="color:${isUp ? '#00e676' : '#ff3366'}">${isUp ? '+' : ''}${t.priceChangePercent.toFixed(2)}%</div>
        </div>
      `;

      card.addEventListener('click', () => {
        currentSymbol = t.symbol;
        chartCoinSelect.value = t.symbol;
        document.querySelector('[data-tab="tabChart"]').click();
        loadCoinAnalysis(currentSymbol, currentTimeframe);
      });

      container.appendChild(card);
    });
  }

  marketSearchInput.addEventListener('input', () => {
    renderMarketList(allTickers);
  });

  chartCoinSelect.addEventListener('change', (e) => {
    currentSymbol = e.target.value;
    loadCoinAnalysis(currentSymbol, currentTimeframe);
  });

  // Signals Feed & Performance Stats
  async function loadAllSignals() {
    const container = document.getElementById('signalsFeedContainer');
    try {
      // Load Stats
      fetch('/api/stats')
        .then(r => r.json())
        .then(stats => {
          if (!stats) return;
          const wrEl = document.getElementById('statWinRate');
          const totEl = document.getElementById('statTotal');
          const winEl = document.getElementById('statWins');
          const lossEl = document.getElementById('statLosses');
          const pnlEl = document.getElementById('statNetPnl');

          if (wrEl) wrEl.textContent = `Win Rate: ${stats.winRatePercent}%`;
          if (totEl) totEl.textContent = stats.totalSignals;
          if (winEl) winEl.textContent = stats.successSignals;
          if (lossEl) lossEl.textContent = stats.failedSignals;
          if (pnlEl) {
            pnlEl.textContent = `${stats.totalNetProfitPercent >= 0 ? '+' : ''}${stats.totalNetProfitPercent}%`;
            pnlEl.style.color = stats.totalNetProfitPercent >= 0 ? 'var(--neon-green)' : 'var(--neon-red)';
          }
        }).catch(() => {});

      const res = await fetch('/api/signals/all');
      if (!res.ok) return;
      allSignals = await res.json();
      renderSignalsFeed();
    } catch (e) {
      container.innerHTML = '<div style="text-align:center; padding:30px; color:var(--neon-red);">Siqnalları yükləmək mümkün olmadı</div>';
    }
  }

  function renderSignalsFeed() {
    const container = document.getElementById('signalsFeedContainer');
    container.innerHTML = '';

    let filtered = allSignals;
    if (activeSignalFilter === 'long') filtered = allSignals.filter(s => s.signalType.includes('LONG'));
    else if (activeSignalFilter === 'short') filtered = allSignals.filter(s => s.signalType.includes('SHORT'));
    else if (activeSignalFilter === 'high') filtered = allSignals.filter(s => (s.confluenceScore || s.confidence) >= 78);

    if (filtered.length === 0) {
      container.innerHTML = '<div style="text-align:center; padding:30px; color:var(--text-muted);">Bu filtrə uyğun aktiv siqnal yoxdur</div>';
      return;
    }

    filtered.forEach(s => {
      const isLong = s.direction === 0 || s.signalType.includes('LONG');
      const isShort = s.direction === 1 || s.signalType.includes('SHORT');
      const cleanName = s.symbol.replace('USDT', '');
      const timeStr = s.timestampFormatted || new Date(s.generatedAt).toLocaleTimeString('az-AZ');
      const score = s.confluenceScore || s.confidence || 80;

      const card = document.createElement('div');
      card.className = 'feed-card ' + (isLong ? 'feed-card-long' : (isShort ? 'feed-card-short' : ''));
      card.innerHTML = `
        <div class="feed-card-header">
          <div style="display:flex; align-items:center; gap:8px;">
            <span style="font-size:13px; font-weight:900; color:var(--neon-cyan); background:rgba(0,240,255,0.1); padding:2px 6px; border-radius:6px;">#${s.signalNumber} ${isLong ? '🟢' : '🔴'}</span>
            <span style="font-size:18px; font-weight:900;">${cleanName}</span>
            <span style="font-size:12px; font-weight:800; color:${isLong ? '#00e676' : '#ff3366'}">${isLong ? 'LONG' : 'SHORT'}</span>
          </div>
          <div style="text-align:right;">
            <div style="font-size:12px; font-weight:800; color:${score>=78?'#00e676':'#00f0ff'}">${score}% Confluence</div>
            <div style="font-size:10px; color:var(--text-muted); margin-top:2px;">🕒 ${timeStr}</div>
          </div>
        </div>

        <div style="display:grid; grid-template-columns:repeat(2, 1fr); gap:8px; margin-bottom:10px;">
          <div style="background:rgba(0,0,0,0.3); padding:8px 10px; border-radius:8px;">
            <div style="font-size:10px; color:var(--text-muted);">GİRİŞ ZONASI:</div>
            <div style="font-family:var(--font-mono); font-size:13px; font-weight:800; color:var(--neon-cyan);">$${s.entryLow} - $${s.entryHigh}</div>
          </div>
          <div style="background:rgba(0,0,0,0.3); padding:8px 10px; border-radius:8px;">
            <div style="font-size:10px; color:var(--text-muted);">STOP LOSS:</div>
            <div style="font-family:var(--font-mono); font-size:13px; font-weight:800; color:var(--neon-red);">$${s.stopLoss}</div>
          </div>
          <div style="background:rgba(0,0,0,0.3); padding:8px 10px; border-radius:8px;">
            <div style="font-size:10px; color:var(--text-muted);">HƏDƏF 1:</div>
            <div style="font-family:var(--font-mono); font-size:13px; font-weight:800; color:var(--neon-green);">$${s.takeProfit1}</div>
          </div>
          <div style="background:rgba(0,0,0,0.3); padding:8px 10px; border-radius:8px;">
            <div style="font-size:10px; color:var(--text-muted);">HƏDƏF 2:</div>
            <div style="font-family:var(--font-mono); font-size:13px; font-weight:800; color:var(--neon-green);">$${s.takeProfit2}</div>
          </div>
        </div>

        <div style="font-size:12px; color:var(--text-secondary); line-height:1.4;">
          ${(s.analysisReasons || []).slice(0, 2).map(r => `<div>▸ ${r}</div>`).join('')}
        </div>
      `;

      card.addEventListener('click', () => {
        currentSymbol = s.symbol;
        chartCoinSelect.value = s.symbol;
        document.querySelector('[data-tab="tabChart"]').click();
        loadCoinAnalysis(currentSymbol, currentTimeframe);
      });

      container.appendChild(card);
    });
  }

  document.querySelectorAll('.filter-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.filter-btn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      activeSignalFilter = btn.getAttribute('data-filter');
      renderSignalsFeed();
    });
  });

  // Single Coin Analysis (Tab 3)
  async function loadCoinAnalysis(symbol, tf) {
    try {
      const klineRes = await fetch(`/api/klines/${symbol}?tf=${tf}`);
      if (klineRes.ok) {
        const klines = await klineRes.json();
        chart.setData(klines);
      }

      const sigRes = await fetch(`/api/analyze/${symbol}?tf=${tf}`);
      if (!sigRes.ok) return;
      const sig = await sigRes.json();

      document.getElementById('activeChartPrice').textContent = '$' + Number(sig.currentPrice).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });

      if (sig.indicators) {
        document.getElementById('hudRsi').textContent = sig.indicators.rsi.toFixed(1);
        document.getElementById('hudRsiSub').textContent = sig.indicators.rsiStatus;
        document.getElementById('hudMacd').textContent = (sig.indicators.macdHist >= 0 ? '+' : '') + sig.indicators.macdHist.toFixed(3);
        document.getElementById('hudMacdSub').textContent = sig.indicators.macdStatus;
        document.getElementById('hudEma').textContent = sig.indicators.emaTrend;
        document.getElementById('hudAtr').textContent = 'ATR: $' + sig.indicators.atr.toFixed(2);
      }

      const card = document.getElementById('aiSignalCard');
      const badge = document.getElementById('signalBadgeBig');
      const isLong = sig.signalType.includes('LONG');
      const isShort = sig.signalType.includes('SHORT');

      badge.textContent = `#${sig.signalNumber} ${isLong ? 'ğŸŸ¢' : (isShort ? 'ğŸ”´' : 'âšª')}`;
      document.getElementById('confidencePill').textContent = sig.confidence + '% Ehtimal';
      card.className = 'glass-card ai-signal-card ' + (isLong ? 'signal-long' : (isShort ? 'signal-short' : 'signal-neutral'));

      document.getElementById('signalSymbolClean').textContent = 'ğŸª™ ' + sig.symbol.replace('USDT', '') + ' Futures';
      document.getElementById('signalTimeBadge').textContent = 'ğŸ•’ ' + (sig.timestampFormatted || new Date().toLocaleString());

      document.getElementById('paramEntry').textContent = `$${sig.entryLow} - $${sig.entryHigh}`;
      document.getElementById('paramTp1').textContent = `$${sig.takeProfit1}`;
      document.getElementById('paramTp2').textContent = `$${sig.takeProfit2}`;
      document.getElementById('paramTp3').textContent = `$${sig.takeProfit3}`;
      document.getElementById('paramSl').textContent = `$${sig.stopLoss}`;

      const reasonsList = document.getElementById('analysisReasonsList');
      reasonsList.innerHTML = '';
      (sig.analysisReasons || []).forEach(r => {
        const li = document.createElement('li');
        li.textContent = r;
        reasonsList.appendChild(li);
      });

    } catch (e) {
      console.error('Coin analysis error', e);
    }
  }

  // Load News
  async function loadNews() {
    const container = document.getElementById('newsFeedList');
    try {
      const res = await fetch('/api/news');
      if (!res.ok) return;
      const data = await res.json();

      document.getElementById('newsOverallSentiment').textContent = data.status;
      document.getElementById('newsSentimentBarometer').textContent = (data.overallScore >= 0 ? '+' : '') + data.overallScore + '% Bal';

      container.innerHTML = '';
      (data.latestNews || []).forEach(item => {
        const card = document.createElement('div');
        card.className = 'news-card';
        card.innerHTML = `
          <div class="news-header">
            <span class="news-source">${item.source}</span>
            <span class="trend-badge ${item.sentiment.includes('BULLISH') || item.sentiment.includes('MUSBET') ? 'trend-bullish' : (item.sentiment.includes('BEARISH') || item.sentiment.includes('MENFI') ? 'trend-bearish' : 'trend-neutral')}">${item.sentiment}</span>
          </div>
          <a href="${item.url}" target="_blank" class="news-title">${item.title}</a>
        `;
        container.appendChild(card);
      });
    } catch (e) {
      console.error('News fetch error', e);
    }
  }

  // ADMIN USER MANAGER (SUPER ADMIN ONLY)
  async function loadAdminUsers() {
    if (userRole !== 'SUPERADMIN') return;
    const container = document.getElementById('adminUserList');
    if (!container) return;
    try {
      const res = await fetch('/api/admin/users');
      if (!res.ok) return;
      const users = await res.json();

      container.innerHTML = '';
      users.forEach(u => {
        const row = document.createElement('div');
        row.style.cssText = 'display:flex; justify-content:space-between; align-items:center; background:rgba(0,0,0,0.3); padding:8px 12px; border-radius:8px; font-size:13px;';
        row.innerHTML = `
          <div>
            <b>${u.username}</b> <span style="font-size:11px; color:var(--text-muted);">(${u.role})</span>
            <div style="font-size:10px; color:var(--text-muted); font-family:var(--font-mono);">Parol: ${u.password}</div>
          </div>
          <div>
            ${u.role === 'SUPERADMIN' ? '<span style="color:var(--neon-cyan); font-weight:800; font-size:11px;">SUPER ADMIN</span>' : `<button data-user="${u.username}" class="btn-del-user" style="background:rgba(255,51,102,0.2); border:1px solid var(--neon-red); color:var(--neon-red); padding:3px 8px; border-radius:6px; font-size:11px; cursor:pointer;">Sil</button>`}
          </div>
        `;
        container.appendChild(row);
      });

      document.querySelectorAll('.btn-del-user').forEach(btn => {
        btn.addEventListener('click', async (e) => {
          const user = e.target.getAttribute('data-user');
          if (confirm(`${user} istifadÉ™Ã§isini silmÉ™k istÉ™diyinizdÉ™n É™minsiniz?`)) {
            await fetch(`/api/admin/users/${user}`, { method: 'DELETE' });
            loadAdminUsers();
          }
        });
      });
    } catch (e) {
      console.error('Admin users load error', e);
    }
  }

  if (btnCreateUser) {
    btnCreateUser.addEventListener('click', async () => {
      const username = document.getElementById('newAdminUsername').value.trim();
      const password = document.getElementById('newAdminPassword').value.trim();
      if (!username || !password) {
        alert('Ä°stifadÉ™Ã§i adÄ± vÉ™ parolu daxil edin!');
        return;
      }
      try {
        const res = await fetch('/api/admin/users', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ username, password })
        });
        const data = await res.json();
        if (res.ok) {
          alert(`Ä°stifadÉ™Ã§i "${username}" uÄŸurla yaradÄ±ldÄ±!`);
          document.getElementById('newAdminUsername').value = '';
          document.getElementById('newAdminPassword').value = '';
          loadAdminUsers();
        } else {
          alert(data.message || 'XÉ™ta baÅŸ verdi');
        }
      } catch (e) {
        alert('XÉ™ta: ' + e.message);
      }
    });
  }

  document.querySelectorAll('.tf-chip').forEach(chip => {
    chip.addEventListener('click', () => {
      document.querySelectorAll('.tf-chip').forEach(c => c.classList.remove('active'));
      chip.classList.add('active');
      currentTimeframe = chip.getAttribute('data-tf');
      loadCoinAnalysis(currentSymbol, currentTimeframe);
    });
  });

  document.getElementById('btnTestTelegram').addEventListener('click', async () => {
    try {
      const res = await fetch('/api/telegram/test', { method: 'POST' });
      const data = await res.json();
      alert(data.message || 'Telegram test siqnali gonderildi!');
    } catch (e) {
      alert('Xeta bas verdi: ' + e.message);
    }
  });
});