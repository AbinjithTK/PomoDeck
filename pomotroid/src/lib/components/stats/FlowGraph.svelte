<script lang="ts">
  import { onMount } from 'svelte';
  import { flowGetTimeline, onFlowChanged, getTimerState } from '$lib/ipc';
  import { listen } from '@tauri-apps/api/event';
  import type { FlowTimelineEntry } from '$lib/ipc';
  import type { FlowState } from '$lib/ipc';
  import type { UnlistenFn } from '@tauri-apps/api/event';

  let { padLeft = 32, padRight = 32, viewWidth = 800 }: {
    padLeft?: number; padRight?: number; viewWidth?: number;
  } = $props();

  let dbEntries = $state<FlowTimelineEntry[]>([]);
  let liveEntry = $state<FlowTimelineEntry | null>(null);
  let animated = $state(false);
  let hoveredDot = $state(-1);
  let hoveredSeg = $state(-1);
  let pathLen = $state(2000);

  // Merge DB entries + live session
  const entries = $derived.by(() => {
    if (!liveEntry) return dbEntries;
    return [...dbEntries, liveEntry];
  });

  // 24-hour axis
  function dayStart(): number {
    const d = new Date(); d.setHours(0, 0, 0, 0);
    return Math.floor(d.getTime() / 1000);
  }
  function dayEnd(): number {
    const d = new Date(); d.setHours(23, 59, 59, 0);
    return Math.floor(d.getTime() / 1000);
  }

  const W = $derived(viewWidth);
  const H = 160;
  const PL = $derived(padLeft);
  const PR = $derived(padRight);
  const PT = 4; const PB = 16;
  const GW = $derived(W - PL - PR);
  const GH = H - PT - PB;

  function tx(ts: number): number {
    const t0 = dayStart(), t1 = dayEnd();
    return PL + Math.max(0, Math.min(1, (ts - t0) / (t1 - t0))) * GW;
  }
  function gy(s: number): number { return PT + GH - (s / 100) * GH; }

  const curvePath = $derived.by(() => {
    if (entries.length === 0) return '';
    const pts = entries.map(e => ({ x: tx(e.started_at + e.duration_secs / 2), y: gy(e.flow_score) }));
    if (pts.length === 1) return `M${pts[0].x},${pts[0].y}`;
    let d = `M${pts[0].x},${pts[0].y}`;
    for (let i = 0; i < pts.length - 1; i++) {
      const p0 = pts[Math.max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.min(pts.length - 1, i + 2)];
      d += ` C${p1.x + (p2.x - p0.x) / 6},${p1.y + (p2.y - p0.y) / 6} ${p2.x - (p3.x - p1.x) / 6},${p2.y - (p3.y - p1.y) / 6} ${p2.x},${p2.y}`;
    }
    return d;
  });

  const areaPath = $derived.by(() => {
    if (!curvePath || entries.length === 0) return '';
    const pts = entries.map(e => tx(e.started_at + e.duration_secs / 2));
    return `${curvePath} L${pts[pts.length - 1]},${PT + GH} L${pts[0]},${PT + GH} Z`;
  });

  const segments = $derived.by(() => {
    if (entries.length === 0) return [];
    const t0 = dayStart(), t1 = dayEnd();
    return entries.map(e => ({
      left: (tx(e.started_at) / W) * 100,
      width: Math.max(0.3, ((e.duration_secs / (t1 - t0)) * GW / W) * 100),
      color: e.task_color || '#555',
      title: e.task_title || 'No task',
      score: e.flow_score,
      mins: Math.round(e.duration_secs / 60),
    }));
  });

  const legend = $derived.by(() => {
    const map = new Map<string, string>();
    for (const e of entries) if (e.task_title && !map.has(e.task_title)) map.set(e.task_title, e.task_color || '#555');
    return [...map.entries()].map(([t, c]) => ({ title: t, color: c }));
  });

  const timeMarks = $derived([0, 3, 6, 9, 12, 15, 18, 21].map(h => ({
    x: PL + (h / 24) * GW,
    label: h === 0 ? '12a' : h === 12 ? '12p' : h < 12 ? `${h}a` : `${h - 12}p`,
  })));

  function fmtTime(ts: number): string { return new Date(ts * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }

  // Track live session start time
  let liveStartedAt = 0;

  async function loadData() {
    try { dbEntries = await flowGetTimeline(); } catch {}
    requestAnimationFrame(() => {
      const el = document.querySelector('.fline') as SVGPathElement | null;
      if (el) pathLen = el.getTotalLength();
      animated = false;
      requestAnimationFrame(() => animated = true);
    });
  }

  function onFlowUpdate(flow: FlowState) {
    if (flow.in_focus) {
      // Update live entry with current flow score
      const now = Math.floor(Date.now() / 1000);
      if (liveStartedAt === 0) liveStartedAt = now;
      liveEntry = {
        started_at: liveStartedAt,
        duration_secs: now - liveStartedAt,
        flow_score: flow.score,
        task_id: null, task_title: null, task_color: null,
      };
    } else {
      // Not in focus — clear live entry
      liveEntry = null;
      liveStartedAt = 0;
    }
  }

  onMount(() => {
    const c: (() => void)[] = [];
    loadData();

    // Check if already in focus
    getTimerState().then(s => {
      if (s.is_running && s.round_type === 'work') {
        liveStartedAt = Math.floor(Date.now() / 1000) - s.elapsed_secs;
      }
    }).catch(() => {});

    (async () => {
      // Reload DB data on round change (session completed → now in DB)
      c.push(await listen('timer:round-change', () => {
        liveEntry = null; liveStartedAt = 0;
        loadData();
      }) as unknown as () => void);
      // Live flow updates every 10s from the backend
      c.push(await onFlowChanged(onFlowUpdate) as unknown as () => void);
    })();
    return () => c.forEach(fn => fn());
  });
</script>

<div class="fs" class:animated>
  <div class="label">FLOW SCORE</div>
  <div class="gwrap">
    <svg viewBox="0 0 {W} {H}" preserveAspectRatio="xMidYMid meet" class="g">
      <defs>
        <linearGradient id="ag" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stop-color="var(--color-accent)" stop-opacity="0.25" />
          <stop offset="100%" stop-color="var(--color-accent)" stop-opacity="0.02" />
        </linearGradient>
      </defs>
      {#each [25, 50, 75] as v}
        <line x1={PL} y1={gy(v)} x2={W - PR} y2={gy(v)} class="grid-line" />
      {/each}
      {#each timeMarks as tm}
        <text x={tm.x} y={H - 1} text-anchor="middle" class="time-label">{tm.label}</text>
      {/each}
      {#each [0, 50, 100] as v}
        <text x={W - 2} y={gy(v) + 3} text-anchor="end" class="sl">{v}</text>
      {/each}
      {#if entries.length > 0}
        <path d={areaPath} fill="url(#ag)" class="area" />
        <path d={curvePath} class="fline" style="stroke-dasharray:{pathLen};stroke-dashoffset:{animated ? 0 : pathLen}" />
        {#each entries as e, i}
          {@const cx = tx(e.started_at + e.duration_secs / 2)}
          {@const isLive = liveEntry && i === entries.length - 1}
          <circle {cx} cy={gy(e.flow_score)} r={hoveredDot === i ? 5 : 3}
            class="dot" class:hov={hoveredDot === i} class:live={isLive}
            style="animation-delay:{i * 30}ms"
            onmouseenter={() => hoveredDot = i} onmouseleave={() => hoveredDot = -1} role="img" />
        {/each}
      {:else}
        <text x={W / 2} y={H / 2} text-anchor="middle" class="empty-text">No focus sessions yet today</text>
      {/if}
    </svg>
    {#if hoveredDot >= 0 && hoveredDot < entries.length}
      {@const e = entries[hoveredDot]}
      {@const cx = tx(e.started_at + e.duration_secs / 2)}
      <div class="tip" style="left:{(cx / W) * 100}%">
        <strong>{e.flow_score}</strong>
        <span>{fmtTime(e.started_at)} · {Math.round(e.duration_secs / 60)}m</span>
        {#if e.task_title}<span class="tt"><i style="background:{e.task_color}"></i>{e.task_title}</span>{/if}
      </div>
    {/if}
  </div>

  {#if entries.length > 0}
    <div class="label">TASK TIMELINE</div>
    <div class="tlwrap">
      <div class="tl">
        {#each segments as seg, i}
          <div class="tseg" style="left:{seg.left}%;width:{seg.width}%;background:{seg.color};animation-delay:{i * 40}ms"
            onmouseenter={() => hoveredSeg = i} onmouseleave={() => hoveredSeg = -1} role="img"></div>
        {/each}
      </div>
      {#if hoveredSeg >= 0 && hoveredSeg < segments.length}
        {@const s = segments[hoveredSeg]}
        <div class="stip" style="left:{s.left + s.width / 2}%">
          <i style="background:{s.color}"></i><strong>{s.title}</strong><span>{s.mins}m · {s.score}%</span>
        </div>
      {/if}
    </div>
    {#if legend.length > 0}
      <div class="leg">{#each legend as t}<span class="li"><i style="background:{t.color}"></i>{t.title}</span>{/each}</div>
    {/if}
  {/if}
</div>

<style>
  .fs { padding: 12px 20px 12px; }
  .label {
    font-size: 0.6rem; font-weight: 600; letter-spacing: 0.1em;
    text-transform: uppercase; color: var(--color-foreground-darker);
    margin-bottom: 6px; margin-top: 4px;
  }
  .gwrap { position: relative; }
  .g { width: 100%; height: auto; display: block; }
  .grid-line { stroke: var(--color-foreground); opacity: 0.05; stroke-width: 1; }
  .time-label { fill: var(--color-foreground-darker); font-size: 9px; opacity: 0.4; }
  .empty-text { fill: var(--color-foreground-darker); font-size: 12px; opacity: 0.3; }
  .area { opacity: 0; transition: opacity 0.6s 0.3s; }
  .animated .area { opacity: 1; }
  .fline {
    fill: none; stroke: var(--color-accent); stroke-width: 2;
    stroke-linecap: round; stroke-linejoin: round;
    transition: stroke-dashoffset 1s cubic-bezier(0.4,0,0.2,1);
  }
  .dot {
    fill: var(--color-foreground-darker); stroke: var(--color-background);
    stroke-width: 1.5; cursor: pointer; opacity: 0; transition: r 0.1s;
  }
  .animated .dot { animation: di 0.3s ease forwards; }
  @keyframes di { to { opacity: 1; } }
  .dot.hov { fill: var(--color-accent); stroke: var(--color-foreground); }
  .dot.live { fill: var(--color-accent); stroke: var(--color-accent); opacity: 1; animation: pulse 1.5s ease infinite; }
  @keyframes pulse { 0%, 100% { r: 3; opacity: 1; } 50% { r: 5; opacity: 0.7; } }
  .sl { fill: var(--color-foreground-darker); font-size: 8px; opacity: 0.4; font-variant-numeric: tabular-nums; }
  .tip {
    position: absolute; top: -8px; transform: translateX(-50%);
    background: var(--color-background-light);
    border: 1px solid var(--color-separator);
    border-radius: 6px; padding: 4px 8px; pointer-events: none;
    box-shadow: 0 4px 12px rgba(0,0,0,0.3); z-index: 10;
    white-space: nowrap; display: flex; flex-direction: column; gap: 1px;
  }
  .tip strong { font-size: 15px; color: var(--color-accent); }
  .tip span { font-size: 9px; color: var(--color-foreground-darker); }
  .tt { display: flex; align-items: center; gap: 4px; color: var(--color-foreground); }
  .tt i, .li i, .stip i { width: 6px; height: 6px; border-radius: 50%; display: inline-block; flex-shrink: 0; }

  .tlwrap { position: relative; margin-bottom: 6px; padding-bottom: 24px; }
  .tl {
    position: relative; height: 14px; border-radius: 3px;
    background: color-mix(in oklch, var(--color-foreground) 4%, transparent);
  }
  .tseg {
    position: absolute; top: 0; height: 100%; min-width: 2px;
    opacity: 0; cursor: pointer; transition: filter 0.1s; border-radius: 2px;
  }
  .tseg:hover { filter: brightness(1.3); }
  .animated .tseg { animation: si 0.4s ease forwards; }
  @keyframes si { to { opacity: 0.85; } }
  .stip {
    position: absolute; top: 20px; transform: translateX(-50%);
    background: var(--color-background-light);
    border: 1px solid var(--color-separator);
    border-radius: 5px; padding: 3px 8px; pointer-events: none;
    box-shadow: 0 3px 10px rgba(0,0,0,0.3); z-index: 10;
    white-space: nowrap; display: flex; align-items: center; gap: 4px; font-size: 10px;
  }
  .stip strong { color: var(--color-foreground); font-weight: 600; }
  .stip span { color: var(--color-foreground-darker); }
  .stip i { width: 7px; height: 7px; border-radius: 2px; }

  .leg { display: flex; gap: 10px; flex-wrap: wrap; padding: 2px 0; }
  .li { display: flex; align-items: center; gap: 4px; font-size: 10px; color: var(--color-foreground-darker); }
  .li i { width: 8px; height: 8px; border-radius: 2px; }
</style>
