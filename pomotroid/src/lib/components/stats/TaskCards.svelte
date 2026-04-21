<script lang="ts">
  import { onMount } from 'svelte';
  import { flowGetTimeline } from '$lib/ipc';
  import { listen } from '@tauri-apps/api/event';
  import type { FlowTimelineEntry } from '$lib/ipc';

  interface TaskStat {
    title: string;
    color: string;
    sessions: number;
    totalMins: number;
    avgFlow: number;
    bestFlow: number;
    entries: FlowTimelineEntry[];
  }

  let tasks = $state<TaskStat[]>([]);
  let revealed = $state(false);

  async function loadData() {
    try {
      const entries = await flowGetTimeline();
      const map = new Map<string, TaskStat>();
      for (const e of entries) {
        const key = e.task_title || 'Unassigned';
        const existing = map.get(key);
        if (existing) {
          existing.sessions++;
          existing.totalMins += e.duration_secs / 60;
          existing.avgFlow = (existing.avgFlow * (existing.sessions - 1) + e.flow_score) / existing.sessions;
          existing.bestFlow = Math.max(existing.bestFlow, e.flow_score);
          existing.entries.push(e);
        } else {
          map.set(key, {
            title: key, color: e.task_color || 'var(--color-foreground-darker)',
            sessions: 1, totalMins: e.duration_secs / 60,
            avgFlow: e.flow_score, bestFlow: e.flow_score, entries: [e],
          });
        }
      }
      tasks = [...map.values()].sort((a, b) => b.totalMins - a.totalMins);
    } catch {}
    setTimeout(() => revealed = true, 50);
  }

  function fmtMins(m: number): string {
    if (m < 60) return `${Math.round(m)}m`;
    return `${Math.floor(m / 60)}h ${Math.round(m % 60)}m`;
  }

  // Hue for flow score: high=red(ripe), low=green(unripe)
  function flowHue(score: number): number {
    return (1 - Math.min(score / 100, 1)) * 120;
  }

  onMount(() => {
    loadData();
    const cleanups: (() => void)[] = [];
    (async () => {
      cleanups.push(await listen('timer:round-change', () => loadData()));
    })();
    return () => cleanups.forEach(fn => fn());
  });
</script>

{#if tasks.length > 0}
<div class="task-cards" class:revealed>
  <div class="section-title">TASKS TODAY</div>

  {#each tasks as task, i}
    <div class="tcard" style="animation-delay:{i * 60}ms">
      <div class="tcard-left" style="background:{task.color}"></div>
      <div class="tcard-body">
        <div class="tcard-header">
          <span class="tcard-name">{task.title}</span>
          <span class="tcard-time">{fmtMins(task.totalMins)}</span>
        </div>
        <div class="tcard-row">
          <div class="tcard-stat">
            <span class="tcard-val">{task.sessions}</span>
            <span class="tcard-label">sessions</span>
          </div>
          <div class="tcard-stat">
            <span class="tcard-val" style="color:hsl({flowHue(task.avgFlow)}, 70%, 55%)">{Math.round(task.avgFlow)}%</span>
            <span class="tcard-label">avg flow</span>
          </div>
          <div class="tcard-stat">
            <span class="tcard-val" style="color:hsl({flowHue(task.bestFlow)}, 70%, 55%)">{Math.round(task.bestFlow)}%</span>
            <span class="tcard-label">best</span>
          </div>
        </div>
        <!-- Mini session dots -->
        <div class="tcard-dots">
          {#each task.entries as e}
            <span class="mini-dot" style="background:hsl({flowHue(e.flow_score)}, 75%, 48%)"
              title="{Math.round(e.duration_secs / 60)}m · {e.flow_score}% flow"></span>
          {/each}
        </div>
      </div>
    </div>
  {/each}
</div>
{/if}

<style>
  .task-cards { padding: 4px 24px 16px; }
  .task-cards.revealed .tcard { animation: tcard-in 0.3s ease forwards; }

  .section-title {
    font-size: 0.68rem; font-weight: 600; letter-spacing: 0.1em;
    text-transform: uppercase; color: var(--color-foreground-darker);
    margin-bottom: 8px;
  }

  .tcard {
    display: flex; border-radius: 8px; margin-bottom: 6px;
    background: var(--color-background-light); overflow: hidden;
    opacity: 0; transform: translateY(4px);
  }
  @keyframes tcard-in {
    to { opacity: 1; transform: translateY(0); }
  }

  .tcard-left { width: 4px; flex-shrink: 0; }
  .tcard-body {
    flex: 1; padding: 10px 12px; display: flex; flex-direction: column; gap: 6px;
  }
  .tcard-header { display: flex; align-items: baseline; justify-content: space-between; }
  .tcard-name { font-size: 13px; font-weight: 600; color: var(--color-foreground); }
  .tcard-time { font-size: 12px; font-weight: 500; color: var(--color-foreground-darker); }

  .tcard-row { display: flex; gap: 16px; }
  .tcard-stat { display: flex; align-items: baseline; gap: 3px; }
  .tcard-val { font-size: 14px; font-weight: 700; color: var(--color-foreground); font-variant-numeric: tabular-nums; }
  .tcard-label { font-size: 10px; color: var(--color-foreground-darker); }

  .tcard-dots { display: flex; gap: 3px; flex-wrap: wrap; }
  .mini-dot {
    width: 8px; height: 8px; border-radius: 50%; cursor: default;
  }
</style>
