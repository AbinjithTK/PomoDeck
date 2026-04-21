<script lang="ts">
  import { onMount } from 'svelte';
  import { taskList, taskListCompleted, taskDelete, taskReopen, onTasksChanged } from '$lib/ipc';
  import type { TaskItem } from '$lib/ipc';

  let active = $state<TaskItem[]>([]);
  let completed = $state<TaskItem[]>([]);
  let showCompleted = $state(false);

  async function refresh() {
    try { active = await taskList(); } catch {}
    try { completed = await taskListCompleted(); } catch {}
  }
  async function remove(id: number) { await taskDelete(id); await refresh(); }
  async function reopen(id: number) { await taskReopen(id); await refresh(); }

  function fmtDur(secs: number): string {
    if (secs < 60) return '<1m';
    const m = Math.round(secs / 60);
    if (m < 60) return `${m}m`;
    const h = Math.floor(m / 60);
    return `${h}h ${m % 60}m`;
  }

  function accuracy(est: number, done: number): string {
    if (est === 0) return '—';
    const pct = Math.round((done / est) * 100);
    return `${pct}%`;
  }

  onMount(() => {
    refresh();
    const c: (() => void)[] = [];
    (async () => { c.push(await onTasksChanged(() => refresh())); })();
    return () => c.forEach(fn => fn());
  });
</script>

<div class="section">
  <div class="group-label">ACTIVE ({active.length})</div>
  {#each active as task}
    <div class="card">
      <div class="tag" style="background:{task.color}"></div>
      <div class="body">
        <div class="row-top">
          <span class="name">{task.title}</span>
          <button class="btn-del" onclick={() => remove(task.id)}>×</button>
        </div>
        <div class="stats">
          <span class="stat"><strong>{task.completed_pomodoros}</strong>/{task.estimated_pomodoros} pomos</span>
          <span class="stat">⏱ {fmtDur(task.elapsed_work_secs)} focused</span>
        </div>
        <div class="bar-track">
          <div class="bar-fill" style="width:{Math.min((task.completed_pomodoros / Math.max(task.estimated_pomodoros, 1)) * 100, 100)}%; background:{task.color}"></div>
        </div>
      </div>
    </div>
  {/each}
  {#if active.length === 0}<div class="empty">No active tasks</div>{/if}

  <button class="toggle" onclick={() => showCompleted = !showCompleted}>
    {showCompleted ? '▾' : '▸'} COMPLETED ({completed.length})
  </button>

  {#if showCompleted}
    {#each completed as task}
      <div class="card done">
        <div class="tag" style="background:{task.color}"></div>
        <div class="body">
          <div class="row-top">
            <span class="name strike">{task.title}</span>
            <div class="actions">
              <button class="btn-act" onclick={() => reopen(task.id)} title="Reopen">↩</button>
              <button class="btn-del" onclick={() => remove(task.id)}>×</button>
            </div>
          </div>
          <div class="stats">
            <span class="stat"><strong>{task.completed_pomodoros}</strong>/{task.estimated_pomodoros} pomos</span>
            <span class="stat">⏱ {fmtDur(task.elapsed_work_secs)}</span>
            <span class="stat acc">{accuracy(task.estimated_pomodoros, task.completed_pomodoros)} accuracy</span>
          </div>
          {#if task.completed_pomodoros > task.estimated_pomodoros}
            <div class="over-badge">+{task.completed_pomodoros - task.estimated_pomodoros} over estimate</div>
          {/if}
        </div>
      </div>
    {/each}
    {#if completed.length === 0}<div class="empty">No completed tasks yet</div>{/if}
  {/if}
</div>

<style>
  .section { padding: 16px 20px; display: flex; flex-direction: column; gap: 5px; }
  .group-label { font-size: 11px; font-weight: 600; letter-spacing: 0.08em; color: var(--color-foreground-darker); margin: 4px 0 2px; }
  .empty { font-size: 12px; color: var(--color-foreground-darker); padding: 8px 0; opacity: 0.5; }

  .card { display: flex; border-radius: 6px; background: var(--color-background-light); overflow: hidden; margin-bottom: 2px; }
  .card.done { opacity: 0.65; }
  .tag { width: 4px; flex-shrink: 0; }
  .body { flex: 1; padding: 8px 10px; display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .row-top { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
  .name { font-size: 13px; font-weight: 500; color: var(--color-foreground); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .strike { text-decoration: line-through; opacity: 0.7; }
  .stats { display: flex; gap: 10px; flex-wrap: wrap; }
  .stat { font-size: 10px; color: var(--color-foreground-darker); }
  .stat strong { color: var(--color-foreground); font-weight: 700; }
  .acc { color: var(--color-accent); font-weight: 500; }
  .over-badge { font-size: 9px; color: #f5c542; font-weight: 600; }

  .bar-track { height: 3px; border-radius: 2px; background: var(--color-separator, rgba(255,255,255,0.06)); overflow: hidden; }
  .bar-fill { height: 100%; border-radius: 2px; transition: width 0.3s; }

  .actions { display: flex; gap: 2px; }
  .btn-del { background: none; border: none; color: var(--color-foreground-darker); cursor: pointer; font-size: 15px; padding: 0 4px; border-radius: 4px; }
  .btn-del:hover { color: #e64137; }
  .btn-act { background: none; border: none; color: var(--color-foreground-darker); cursor: pointer; font-size: 13px; padding: 0 4px; border-radius: 4px; }
  .btn-act:hover { color: var(--color-accent); }

  .toggle { background: none; border: none; cursor: pointer; font-size: 11px; font-weight: 600; letter-spacing: 0.08em; color: var(--color-foreground-darker); text-align: left; padding: 8px 0 2px; }
  .toggle:hover { color: var(--color-foreground); }
</style>
