<script lang="ts">
  import type { DailyStats } from '$lib/types';
  import FlowGraph from './FlowGraph.svelte';
  import * as m from '$paraglide/messages.js';

  let { today }: { today: DailyStats | null } = $props();
  let copied = $state(false);

  async function copyStats() {
    if (!today) return;
    const text = `PomoDeck — Today\n🍅 ${today.rounds} rounds\n⏱ ${fmtTime(today.focus_mins)} focus\n✅ ${fmtRate(today.completion_rate)} completion`;
    try { await navigator.clipboard.writeText(text); copied = true; setTimeout(() => copied = false, 1500); } catch {}
  }

  function fmtTime(mins: number): string {
    if (mins < 60) return `${mins}m`;
    const h = Math.floor(mins / 60);
    const r = mins % 60;
    return r === 0 ? `${h}h` : `${h}h ${r}m`;
  }
  function fmtRate(rate: number | null): string {
    return rate === null ? '—' : `${Math.round(rate * 100)}%`;
  }

  const byHour = $derived(today?.by_hour ?? Array(24).fill(0));
  const maxHour = $derived(Math.max(1, ...byHour));
  const hasData = $derived(today !== null && today.rounds > 0);

  // Shared axis constants — must match FlowGraph
  const W = 800;
  const PL = 32; const PR = 32;
  const GW = W - PL - PR;
  const BAR_H = 80;
  const BAR_W = GW / 24 - 2; // bar width with 2px gap

  function hourX(h: number): number { return PL + (h / 24) * GW; }

  const timeMarks = [0, 3, 6, 9, 12, 15, 18, 21].map(h => ({
    x: hourX(h),
    label: h === 0 ? '12a' : h === 12 ? '12p' : h < 12 ? `${h}a` : `${h - 12}p`,
  }));
</script>

<div class="view">
  <div class="cards">
    <div class="card">
      <span class="card-label">{m.stats_rounds()}</span>
      <span class="card-value">{today?.rounds ?? '—'}</span>
    </div>
    <div class="sep"></div>
    <div class="card">
      <span class="card-label">{m.stats_focus_time()}</span>
      <span class="card-value">{today ? fmtTime(today.focus_mins) : '—'}</span>
    </div>
    <div class="sep"></div>
    <div class="card">
      <span class="card-label">{m.stats_completion()}</span>
      <span class="card-value">{today ? fmtRate(today.completion_rate) : '—'}</span>
    </div>
  </div>

  <div class="section">
    <div class="section-header">
      <span class="section-title">{m.stats_sessions_by_hour()}</span>
      {#if hasData}
        <button class="copy-btn" onclick={copyStats}>{copied ? '✓ Copied' : '📋 Copy'}</button>
      {/if}
      {#if !hasData}<span class="empty-hint">{m.stats_no_sessions_today()}</span>{/if}
    </div>
    <div class="chart-wrap">
      <svg viewBox="0 0 {W} {BAR_H + 20}" preserveAspectRatio="xMidYMid meet" class="chart">
        {#each byHour as count, h}
          {@const barH = count > 0 ? Math.max(4, (count / maxHour) * BAR_H) : 2}
          {@const x = hourX(h) + 1}
          <rect
            {x} y={BAR_H - barH} width={BAR_W} height={barH} rx="2"
            class="bar" class:bar-empty={count === 0}
            style="--d:{h * 15}ms"
          />
          {#if count > 0}
            <text x={x + BAR_W / 2} y={BAR_H - barH - 3} text-anchor="middle" class="count-label">{count}</text>
          {/if}
        {/each}
        <line x1={PL} y1={BAR_H} x2={W - PR} y2={BAR_H} class="baseline" />
        {#each timeMarks as tm}
          <text x={tm.x} y={BAR_H + 14} text-anchor="middle" class="time-label">{tm.label}</text>
        {/each}
      </svg>
    </div>
  </div>

  <FlowGraph padLeft={PL} padRight={PR} viewWidth={W} />
</div>

<style>
  .view { display: flex; flex-direction: column; min-height: 100%; animation: app-fade-in 0.2s ease; }
  .cards { display: flex; align-items: stretch; border-bottom: 1px solid var(--color-separator); flex-shrink: 0; }
  .card {
    flex: 1; display: flex; flex-direction: column; align-items: center;
    justify-content: center; gap: 4px; padding: 18px 8px;
  }
  .card-label {
    font-size: 0.6rem; font-weight: 600; letter-spacing: 0.1em;
    text-transform: uppercase; color: var(--color-foreground-darker);
  }
  .card-value {
    font-size: 1.6rem; font-weight: 700; font-variant-numeric: tabular-nums;
    color: var(--color-foreground); line-height: 1;
  }
  .sep { width: 1px; background: var(--color-separator); margin: 8px 0; }

  .section { padding: 12px 20px 0; display: flex; flex-direction: column; gap: 4px; }
  .section-header { display: flex; align-items: baseline; gap: 10px; }
  .section-title {
    font-size: 0.6rem; font-weight: 600; letter-spacing: 0.1em;
    text-transform: uppercase; color: var(--color-foreground-darker);
  }
  .empty-hint { font-size: 0.7rem; color: var(--color-foreground-darker); opacity: 0.5; font-style: italic; }
  .copy-btn {
    margin-left: auto; background: none; border: 1px solid var(--color-separator);
    color: var(--color-foreground-darker); font-size: 10px; padding: 2px 8px;
    border-radius: 4px; cursor: pointer; transition: all 0.12s;
  }
  .copy-btn:hover { color: var(--color-accent); border-color: var(--color-accent); }

  .chart-wrap { overflow: hidden; }
  .chart { width: 100%; height: auto; display: block; }
  .bar {
    fill: var(--color-focus-round); opacity: 0.85;
    transform-origin: bottom; transform-box: fill-box;
    animation: bar-up 0.4s cubic-bezier(0.22,1,0.36,1) both;
    animation-delay: var(--d, 0ms);
  }
  @keyframes bar-up { from { transform: scaleY(0); opacity: 0; } to { transform: scaleY(1); opacity: 0.85; } }
  .bar-empty { fill: var(--color-foreground); opacity: 0.04; animation: none; }
  .count-label {
    fill: var(--color-foreground-darker); font-size: 8px; font-weight: 600;
    font-variant-numeric: tabular-nums;
  }
  .time-label { fill: var(--color-foreground-darker); font-size: 9px; opacity: 0.4; }
  .baseline { stroke: var(--color-separator); stroke-width: 0.5; }
</style>
