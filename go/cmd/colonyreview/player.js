// The run page's player: one image, a slider over the recorded hours, and
// panels that follow the chosen hour. DATA is the report's frames (see
// frameDTO in html.go).
(function () {
  'use strict';
  var F = DATA.frames, N = F.length;
  var $ = function (id) { return document.getElementById(id); };
  if (!N) { $('player').hidden = true; return; }
  $('empty').hidden = true;

  var cur = -1, timer = 0, view = 'colony', speed = 8;
  var shot = $('shot'), seek = $('seek');
  seek.max = N - 1;

  function esc(s) {
    return String(s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }
  function pct(v) { return v == null ? '–' : Math.round(v * 100) + '%'; }
  function num(v, d) { return v == null ? '–' : v.toFixed(d); }
  function pos(i) { return N < 2 ? 0 : i / (N - 1) * 100; }

  // Series for the charts: [label, value(frame), format, bad(frame value)].
  var series = [
    ['Colonists', function (f) { return f.n; }, function (v) { return v.toFixed(0); }],
    ['Mean mood', function (f) { return f.mood == null ? null : f.mood * 100; }, function (v) { return v.toFixed(0) + '%'; }],
    ['Food runway', function (f) { return f.food; }, function (v) { return v.toFixed(1) + ' d'; }],
    ['Wealth', function (f) { return f.wealth; }, function (v) { return v.toFixed(0); }],
    ['Concerns in deficit', function (f) { return f.n == null ? null : (f.u || []).length; }, function (v) { return v.toFixed(0); }]
  ];

  // Click or drag on a plot seeks to that hour.
  function attach(plot) {
    var drag = function (e) {
      var r = plot.getBoundingClientRect();
      go(Math.round(Math.min(1, Math.max(0, (e.clientX - r.left) / r.width)) * (N - 1)), true);
    };
    plot.addEventListener('pointerdown', function (e) { plot.setPointerCapture(e.pointerId); drag(e); });
    plot.addEventListener('pointermove', function (e) { if (plot.hasPointerCapture(e.pointerId)) drag(e); });
  }

  // ---- charts: one polyline per series and a cursor shared by all of them.
  var chartsEl = $('charts'), cursors = [], readouts = [];
  series.forEach(function (s, k) {
    var vals = F.map(s[1]), lo = Infinity, hi = -Infinity;
    vals.forEach(function (v) { if (v != null) { lo = Math.min(lo, v); hi = Math.max(hi, v); } });
    if (lo === Infinity) { lo = 0; hi = 1; }
    var span = hi - lo || 1, d = '', pen = false;
    vals.forEach(function (v, i) {
      if (v == null) { pen = false; return; }
      d += (pen ? 'L' : 'M') + i + ',' + (95 - 90 * (v - lo) / span).toFixed(1);
      pen = true;
    });
    var box = document.createElement('div');
    box.className = 'chart';
    box.innerHTML = '<div class="chead"><span class="muted">' + esc(s[0]) + '</span><b></b></div>' +
      '<div class="plot"><svg viewBox="0 0 ' + Math.max(1, N - 1) + ' 100" preserveAspectRatio="none"><path d="' + d + '"/></svg>' +
      '<i class="cursor"></i></div><div class="crange muted"><span>' + (lo === Infinity ? '' : s[2](lo)) + '</span><span>' + s[2](hi) + '</span></div>';
    chartsEl.appendChild(box);
    attach(box.querySelector('.plot'));
    cursors.push(box.querySelector('.cursor'));
    readouts.push(box.querySelector('b'));
  });

  // ---- mood strip: a row per colonist (keyed on the name before the job
  // title, which can change), a cell per hour colored by that pawn's mood.
  var names = [], byName = {};
  F.forEach(function (f, i) {
    (f.p || []).forEach(function (p) {
      var n = p.l.split(',')[0];
      if (!byName[n]) { byName[n] = []; names.push(n); }
      byName[n][i] = p;
    });
  });
  var strips = [];
  if (names.length) {
    var sbox = document.createElement('div');
    sbox.className = 'chart';
    sbox.innerHTML = '<div class="chead"><span class="muted">Mood by colonist</span></div>';
    names.forEach(function (n) {
      var row = document.createElement('div');
      row.className = 'strip';
      row.innerHTML = '<span class="sname" title="' + esc(n) + '">' + esc(n) + '</span><div class="plot"><canvas></canvas><i class="cursor"></i></div>';
      sbox.appendChild(row);
      var plot = row.querySelector('.plot');
      attach(plot);
      cursors.push(row.querySelector('.cursor'));
      strips.push([byName[n], plot.querySelector('canvas')]);
    });
    chartsEl.appendChild(sbox);
  }
  function css(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }
  function drawStrips() {
    var bad = css('--bad'), warn = css('--warn'), ok = css('--accent'), fg = css('--fg');
    strips.forEach(function (st) {
      var c = st[1], dpr = window.devicePixelRatio || 1, w = c.clientWidth, h = c.clientHeight;
      c.width = Math.max(1, Math.round(w * dpr)); c.height = Math.max(1, Math.round(h * dpr));
      var x = c.getContext('2d'), cw = c.width / N;
      st[0].forEach(function (p, i) {
        if (!p || p.m == null) return;
        x.fillStyle = p.m < 0.25 ? bad : p.m < 0.4 ? warn : ok;
        x.globalAlpha = p.m < 0.4 ? 1 : 0.35 + 0.65 * Math.min(1, p.m);
        x.fillRect(Math.floor(i * cw), 0, Math.ceil(cw), c.height);
        if (p.d) { x.globalAlpha = 1; x.fillStyle = fg; x.fillRect(Math.floor(i * cw), c.height * 0.4, Math.ceil(cw), c.height * 0.2); }
      });
    });
  }
  drawStrips();
  window.addEventListener('resize', drawStrips);
  if (window.matchMedia) window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', drawStrips);

  // ---- slider marks: a tick per day, a dot per flagged hour.
  var marks = '';
  F.forEach(function (f, i) {
    if (i === 0 || F[i - 1].d !== f.d) marks += '<span class="day" style="left:' + pos(i) + '%">' + f.d + '</span>';
    if (f.f && f.f.length) marks += '<i class="flag ' + (f.f.some(function (x) { return x.s === 'bad'; }) ? 'bad' : 'warn') + '" style="left:' + pos(i) + '%"></i>';
  });
  $('marks').innerHTML = marks;

  // ---- frames
  var cache = {};
  function url(name) { return 'review/' + name; }
  function preload(name) {
    if (name && !cache[name]) { var im = new Image(); im.src = url(name); cache[name] = im; }
  }
  function mapIndex(i) {
    for (var j = i; j >= 0; j--) if (F[j].m) return j;
    for (var k = i; k < N; k++) if (F[k].m) return k;
    return -1;
  }
  function frameImage(i) { return view === 'map' ? (F[mapIndex(i)] || {}).m : F[i].s; }

  function go(i, user) {
    i = Math.max(0, Math.min(N - 1, i));
    if (user) stop();
    if (i === cur) return;
    cur = i;
    var f = F[i], name = frameImage(i);
    if (name) { shot.src = url(name); shot.hidden = false; } else { shot.hidden = true; }
    for (var k = 1; k <= 12; k++) if (i + k < N) preload(view === 'map' ? (F[mapIndex(i + k)] || {}).m : F[i + k].s);
    seek.value = i;
    $('clock').textContent = f.l;
    $('when').textContent = f.l;
    cursors.forEach(function (c) { c.style.left = pos(i) + '%'; });
    series.forEach(function (s, k) {
      var v = s[1](f);
      readouts[k].textContent = v == null ? '–' : s[2](v);
    });
    renderNow(f, i);
    document.querySelectorAll('#flaglist li.on').forEach(function (e) { e.classList.remove('on'); });
    var li = document.querySelector('#flaglist li[data-t="' + f.t + '"]');
    if (li) li.classList.add('on');
  }

  function renderNow(f, i) {
    var h = '<h2>' + esc(f.l) + ' <span class="muted">tick ' + f.t + (f.sam ? ' · facts from ' + esc(f.sam) : '') + '</span></h2>';
    if (f.err) h += '<p class="muted">No colony readings this hour (the census call timed out).</p>';
    var foodBad = f.food != null && f.food < 2, moodBad = f.mood != null && f.mood < 0.25;
    h += '<div class="tiles">' +
      tile('Colonists', f.n == null ? '–' : f.n, i > 0 && F[i - 1].n != null && f.n != null && f.n < F[i - 1].n ? 'bad' : '') +
      tile('Mean mood', pct(f.mood), moodBad ? 'bad' : '') +
      tile('Food runway', f.food == null ? '–' : num(f.food, 1) + ' d', foodBad ? 'bad' : '') +
      tile('Wealth', f.wealth == null ? '–' : f.wealth.toFixed(0), '') +
      tile('Build tier', f.tier ? esc(f.tier) : '–', '') +
      tile('In deficit', f.n == null ? '–' : (f.u || []).length, (f.u || []).length ? 'warn' : '') + '</div>';
    if (f.p && f.p.length) {
      h += '<table class="pawns"><tr><th></th><th>Mood</th><th>Food</th></tr>';
      f.p.forEach(function (p) {
        h += '<tr><td>' + esc(p.l) + (p.d ? ' <span class="bad">downed</span>' : '') + '</td><td>' + bar(p.m, p.m != null && p.m < 0.25 ? 'bad' : p.m != null && p.m < 0.4 ? 'warn' : '') +
          '</td><td>' + bar(p.f, '') + '</td></tr>';
      });
      h += '</table>';
    }
    if (f.f && f.f.length) {
      h += '<ul class="notes">' + f.f.map(function (x) { return '<li class="' + x.s + '">⚑ ' + esc(x.t) + '</li>'; }).join('') + '</ul>';
    }
    if (f.c && f.c.length) h += '<ul class="notes muted">' + f.c.map(function (x) { return '<li>' + esc(x) + '</li>'; }).join('') + '</ul>';
    if (f.u && f.u.length) {
      h += '<p class="chips"><span class="muted">In deficit:</span> ' + f.u.map(function (u) {
        return '<span class="chip" title="' + esc(u[1]) + '">' + esc(u[0]) + '</span>';
      }).join('') + '</p>';
    }
    if (f.z && f.z.length || f.fb) {
      h += '<p class="muted">Storage: ' + (f.z || []).map(function (z) { return esc(z.r) + ' ' + z.z + (z.c ? ' (' + z.u + '/' + z.c + ' cells)' : ''); }).join(' · ') +
        (f.fb ? ' · <span class="warn">starting supplies forbidden</span>' : '') + '</p>';
    }
    $('now').innerHTML = h;
  }
  function tile(label, value, cls) { return '<div class="tile"><span class="muted">' + label + '</span><b class="' + cls + '">' + value + '</b></div>'; }
  function bar(v, cls) {
    if (v == null) return '<span class="muted">–</span>';
    return '<span class="bar ' + cls + '"><i style="width:' + Math.round(v * 100) + '%"></i></span> <span class="barv">' + pct(v) + '</span>';
  }

  // ---- transport
  function stop() {
    if (timer) { clearInterval(timer); timer = 0; }
    $('play').textContent = '▶';
    $('play').title = 'Play (space)';
    if (cur >= 0) try { history.replaceState(null, '', '#t' + F[cur].t); } catch (e) { }
  }
  function play() {
    if (cur >= N - 1) go(0);
    $('play').textContent = '⏸';
    $('play').title = 'Pause (space)';
    timer = setInterval(function () {
      if (cur >= N - 1) { stop(); return; }
      go(cur + 1);
    }, 1000 / speed);
  }
  function toggle() { timer ? stop() : play(); }
  function dayStep(dir) {
    var d = F[cur].d, i = cur;
    if (dir > 0) { while (i < N - 1 && F[i].d === d) i++; return i; }
    while (i > 0 && F[i].d === d) i--;
    while (i > 0 && F[i - 1].d === F[i].d) i--;
    return i;
  }
  function flagStep(dir) {
    for (var i = cur + dir; i >= 0 && i < N; i += dir) if (F[i].f && F[i].f.length) return i;
    return cur;
  }
  function fromTick(t) { for (var i = 0; i < N; i++) if (F[i].t === t) return i; return -1; }

  $('play').addEventListener('click', toggle);
  $('prev').addEventListener('click', function () { go(cur - 1, true); });
  $('next').addEventListener('click', function () { go(cur + 1, true); });
  $('prevday').addEventListener('click', function () { go(dayStep(-1), true); });
  $('nextday').addEventListener('click', function () { go(dayStep(1), true); });
  $('prevflag').addEventListener('click', function () { go(flagStep(-1), true); });
  $('nextflag').addEventListener('click', function () { go(flagStep(1), true); });
  seek.addEventListener('input', function () { go(+seek.value, true); });
  $('speed').addEventListener('change', function (e) {
    speed = +e.target.value;
    if (timer) { stop(); play(); }
  });
  document.querySelectorAll('#vtoggle button').forEach(function (b) {
    b.addEventListener('click', function () {
      view = b.dataset.view;
      document.querySelectorAll('#vtoggle button').forEach(function (o) { o.classList.toggle('on', o === b); });
      var i = cur; cur = -1; go(i);
    });
  });
  document.addEventListener('keydown', function (e) {
    var t = e.target.tagName;
    if (t === 'SELECT' || t === 'TEXTAREA' || e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.key === ' ' && t !== 'BUTTON') { e.preventDefault(); toggle(); }
    else if (e.key === 'ArrowLeft') { e.preventDefault(); go(e.shiftKey ? dayStep(-1) : cur - 1, true); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); go(e.shiftKey ? dayStep(1) : cur + 1, true); }
    else if (e.key === '[') go(flagStep(-1), true);
    else if (e.key === ']') go(flagStep(1), true);
    else if (e.key === 'Home') go(0, true);
    else if (e.key === 'End') go(N - 1, true);
  });
  document.querySelectorAll('#flaglist a').forEach(function (a) {
    a.addEventListener('click', function (e) {
      var i = fromTick(+a.getAttribute('href').slice(2));
      if (i >= 0) { e.preventDefault(); go(i, true); $('player').scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }
    });
  });
  window.addEventListener('hashchange', function () {
    var m = /^#t(\d+)$/.exec(location.hash), i = m ? fromTick(+m[1]) : -1;
    if (i >= 0) go(i, true);
  });

  var m = /^#t(\d+)$/.exec(location.hash), start = m ? fromTick(+m[1]) : -1;
  go(start >= 0 ? start : 0);
})();
