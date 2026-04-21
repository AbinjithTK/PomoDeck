<script lang="ts">
  import type { HeatmapStats } from '$lib/types';
  import * as m from '$paraglide/messages.js';

  let { heatmap }: { heatmap: HeatmapStats | null } = $props();

  const CELL = 11;
  const GAP = 2;
  const STEP = CELL + GAP;

  // Build count map once
  const countMap = $derived.by(() => {
    const map = new Map<string, number>();
    if (heatmap) for (const e of heatmap.entries) map.set(e.date, e.count);
    return map;
  });

  const maxCount = $derived(Math.max(1, ...(heatmap?.entries.map(e => e.count) ?? [1])));

  // 52-week grid ending today, like GitHub contributions
  const grid = $derived.by(() => {
    const today = new Date();
    today.setHours(0, 0, 0, 0);

    // Find the start: go back 52 weeks from the start of this week (Sunday)
    const todayDay = today.getDay(); // 0=Sun
    const endOfWeek = new Date(today);
    const startDate = new Date(today);
    startDate.setDate(today.getDate() - 52 * 7 - todayDay);

    const cells: { key: string; count: number; col: number; row: number }[] = [];
    const months: { label: string; x: number }[] = [];
    let prevMonth = -1;

    const d = new Date(startDate);
    let col = 0;

    while (d <= endOfWeek) {
      const row = d.getDay(); // 0=Sun at top
      const key = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

      // Track month labels (on first day of month that falls on row 0 or first cell of a new col)
      if (d.getMonth() !== prevMonth) {
        prevMonth = d.getMonth();
        months.push({ label: d.toLocaleString('default', { month: 'short' }), x: col * STEP });
      }

      cells.push({ key, count: countMap.get(key) ?? 0, col, row });

      d.setDate(d.getDate() + 1);
      // New week = new column (after Saturday)
      if (d.getDay() === 0 && d <= endOfWeek) col++;
    }

    return { cells, months, cols: col + 1 };
  });

  function level(count: number): number {
    if (count === 0) return 0;
    const r = count / maxCount;
    if (r <= 0.25) return 1;
    if (r <= 0.5) return 2;
    if (r <= 0.75) return 3;
    return 4;
  }

  // Tooltip
  let tip = $state<{ x: number; y: number; date: string; count: number } | null>(null);

  function fmtDate(d: string): string {
    try {
      return new Date(d + 'T12:00:00').toLocaleDateString('default', {
        weekday: 'short', month: 'short', day: 'numeric', year: 'numeric'
      });
    } catch { return d; }
  }
</script>

<div class="view">
  <div class="cards">
    <div class="card">
      <span class="card-label">{m.stats_rounds()}</span>
      <span class="card-value">{heatmap?.total_rounds ?? '—'}</span>
    </div>
    <div class="sep"></div>
    <div class="card">
      <span class="card-label">{m.stats_focus_time()}</span>
      <span class="card-value">{heatmap ? `${heatmap.total_hours}h` : '—'}</span>
    </div>
    <div class="sep"></div>
    <div class="card">
      <span class="card-label">BEST STREAK</span>
      <span class="card-value">{heatmap?.longest_streak ?? '—'}</span>
    </div>
  </div>

  <div class="heat-section">
    <div class="heat-header">
      <span class="heat-title">ACTIVITY</span>
      <div class="legend">
        <span class="leg-text">Less</span>
        {#each [0,1,2,3,4] as lv}
          <span class="leg-cell lv{lv}"></span>
        {/each}
        <span class="leg-text">More</span>
      </div>
    </div>

    <div class="heat-scroll">
      <div class="heat-grid">
        <!-- Month labels -->
        <svg width={grid.cols * STEP} height={12} class="month-row">
          {#each grid.months as mo}
            <text x={mo.x} y={10} class="mo-label">{mo.label}</text>
          {/each}
        </svg>

        <!-- Heatmap cells -->
        <svg width={grid.cols * STEP} height={7 * STEP} class="cell-grid">
          {#each grid.cells as cell}
            <rect
              x={cell.col * STEP} y={cell.row * STEP}
              width={CELL} height={CELL} rx="2"
              class="cell lv{level(cell.count)}"
              onmouseenter={(e) => { tip = { x: e.clientX, y: e.clientY, date: cell.key, count: cell.count }; }}
              onmouseleave={() => { tip = null; }}
            />
          {/each}
        </svg>
      </div>
    </div>
  </div>
</div>

{#if tip}
  <div class="tip" style="left:{tip.x + 10}px;top:{tip.y - 36}px">
    <strong>{tip.count} pomo{tip.count !== 1 ? 's' : ''}</strong>
    <span>{fmtDate(tip.date)}</span>
  </div>
{/if}

<style>
  .view {
    display: flex; flex-direction: column; height: 100%;
    animation: app-fade-in 0.2s ease;
  }
  .cards {
    display: flex; align-items: stretch;
    border-bottom: 1px solid var(--color-separator); flex-shrink: 0;
  }
  .card {
    flex: 1; display: flex; flex-direction: column; align-items: center;
    justify-content: center; gap: 5px; padding: 22px 12px;
  }
  .card-label {
    font-size: 0.63rem; font-weight: 600; letter-spacing: 0.1em;
    text-transform: uppercase; color: var(--color-foreground-darker);
  }
  .card-value {
    font-size: 2rem; font-weight: 700; font-variant-numeric: tabular-nums;
    color: var(--color-foreground); line-height: 1;
  }
  .sep { width: 1px; background: var(--color-separator); margin: 10px 0; }

  .heat-section {
    flex: 1; padding: 16px 20px 16px; display: flex; flex-direction: column;
    gap: 10px; min-height: 0;
  }
  .heat-header {
    display: flex; align-items: center; justify-content: space-between;
  }
  .heat-title {
    font-size: 0.63rem; font-weight: 600; letter-spacing: 0.1em;
    text-transform: uppercase; color: var(--color-foreground-darker);
  }
  .legend { display: flex; align-items: center; gap: 3px; }
  .leg-text { font-size: 9px; color: var(--color-foreground-darker); opacity: 0.5; padding: 0 2px; }
  .leg-cell {
    width: 10px; height: 10px; border-radius: 2px; display: inline-block;
  }
  .leg-cell.lv0 { background: var(--color-foreground); opacity: 0.06; }
  .leg-cell.lv1 { background: var(--color-accent); opacity: 0.3; }
  .leg-cell.lv2 { background: var(--color-accent); opacity: 0.5; }
  .leg-cell.lv3 { background: var(--color-accent); opacity: 0.75; }
  .leg-cell.lv4 { background: var(--color-accent); opacity: 1; }

  .heat-scroll {
    flex: 1; overflow-x: auto; overflow-y: hidden;
    scrollbar-width: none; -ms-overflow-style: none;
  }
  .heat-scroll::-webkit-scrollbar { display: none; }
  .heat-grid { display: flex; flex-direction: column; }
  .month-row, .cell-grid { display: block; }
  .mo-label { fill: var(--color-foreground-darker); font-size: 9px; opacity: 0.6; }

  .cell { transition: opacity 0.08s; cursor: default; }
  .cell.lv0 { fill: var(--color-foreground); opacity: 0.06; }
  .cell.lv1 { fill: var(--color-accent); opacity: 0.3; }
  .cell.lv2 { fill: var(--color-accent); opacity: 0.5; }
  .cell.lv3 { fill: var(--color-accent); opacity: 0.75; }
  .cell.lv4 { fill: var(--color-accent); opacity: 1; }
  .cell:hover { stroke: var(--color-foreground); stroke-width: 1; }

  .tip {
    position: fixed; z-index: 9999;
    background: var(--color-background-light);
    border: 1px solid var(--color-separator);
    border-radius: 6px; padding: 5px 10px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.3);
    display: flex; flex-direction: column; gap: 1px;
    pointer-events: none;
  }
  .tip strong { font-size: 11px; color: var(--color-foreground); }
  .tip span { font-size: 9px; color: var(--color-foreground-darker); }
</style>
