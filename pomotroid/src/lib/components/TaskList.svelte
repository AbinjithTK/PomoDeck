<script lang="ts">
  import { onMount } from 'svelte';
  import { listen } from '@tauri-apps/api/event';
  import { taskList, taskCreate, taskSetActive, taskComplete, onTasksChanged } from '$lib/ipc';
  import type { TaskItem } from '$lib/ipc';

  let tasks = $state<TaskItem[]>([]);
  let newTitle = $state('');
  let newEstimate = $state(1);
  let inputEl: HTMLInputElement | undefined = $state();
  let bottomEl: HTMLDivElement | undefined = $state();

  async function refresh() { try { tasks = await taskList(); } catch {} }
  async function addTask() {
    const t = newTitle.trim(); if (!t) return;
    await taskCreate(t, newEstimate); newTitle = ''; newEstimate = 1;
    await refresh();
    setTimeout(() => bottomEl?.scrollIntoView({ behavior: 'smooth' }), 100);
  }
  async function activate(id: number) { await taskSetActive(id); await refresh(); }
  async function complete(id: number) { await taskComplete(id); await refresh(); }
  function handleKey(e: KeyboardEvent) {
    if (e.key === 'Enter') addTask();
    else if (e.key === 'ArrowLeft') { e.preventDefault(); newEstimate = Math.max(1, newEstimate - 1); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); newEstimate = Math.min(8, newEstimate + 1); }
  }
  function focusInput() {
    inputEl?.focus();
    setTimeout(() => bottomEl?.scrollIntoView({ behavior: 'smooth' }), 50);
  }

  let totalEst = $derived(tasks.reduce((s, t) => s + t.estimated_pomodoros, 0));
  let totalDone = $derived(tasks.reduce((s, t) => s + t.completed_pomodoros, 0));

  onMount(() => {
    const c: (() => void)[] = [];
    refresh();
    (async () => {
      c.push(await onTasksChanged(() => refresh()));
      c.push(await listen('navigate:tasks', () => focusInput()));
    })();
    return () => c.forEach(fn => fn());
  });
</script>

<div class="tasks">
  <div class="header">Tasks</div>

  {#each tasks as task (task.id)}
    <div class="card" class:active={task.is_active}
      style="--task-color:{task.color || 'var(--color-accent)'}"
      role="button" tabindex="0"
      onclick={() => activate(task.id)}
      onkeydown={(e) => { if (e.key === 'Enter') activate(task.id); }}>
      <div class="card-inner">
        <button class="circle" onclick={(e) => { e.stopPropagation(); complete(task.id); }}
          aria-label="Done"></button>
        <div class="card-content">
          <div class="card-top">
            <span class="task-name">{task.title}</span>
            <span class="pomo-count" class:over={task.completed_pomodoros > task.estimated_pomodoros}>
              {task.completed_pomodoros}/{task.estimated_pomodoros}
            </span>
          </div>
          <div class="pomo-dots">
            {#each Array(Math.max(task.estimated_pomodoros, task.completed_pomodoros)) as _, i}
              {#if i < task.completed_pomodoros}
                <span class="tomato filled">🍅</span>
              {:else}
                <span class="tomato empty">🍅</span>
              {/if}
            {/each}
            {#if task.completed_pomodoros > task.estimated_pomodoros}
              <span class="over-label">+{task.completed_pomodoros - task.estimated_pomodoros}</span>
            {/if}
          </div>
        </div>
      </div>
    </div>
  {/each}

  <div class="add-row">
    <input bind:this={inputEl} bind:value={newTitle} onkeydown={handleKey}
      placeholder="+ Add Task" class="add-input" />
    <div class="pomo-picker">
      <button class="pomo-btn" onclick={() => newEstimate = Math.max(1, newEstimate - 1)} aria-label="Less">◀</button>
      <span class="pomo-display">{#each Array(Math.min(newEstimate, 5)) as _}🍅{/each} {newEstimate}</span>
      <button class="pomo-btn" onclick={() => newEstimate = Math.min(8, newEstimate + 1)} aria-label="More">▶</button>
    </div>
  </div>

  {#if tasks.length > 0}
    <div class="footer">Pomos: <strong>{totalDone}</strong> / <strong>{totalEst}</strong></div>
  {/if}
  <div bind:this={bottomEl}></div>
</div>

<style>
  .tasks { width: 100%; padding: 0 12px 8px; }
  .header {
    font-size: 14px; font-weight: 700; color: var(--color-foreground);
    padding: 8px 4px 6px;
  }
  .card {
    border-radius: 8px; margin-bottom: 4px;
    background: color-mix(in srgb, var(--color-foreground) 8%, transparent);
    cursor: pointer; transition: background 0.1s;
    border-left: 3px solid transparent;
  }
  .card:hover {
    background: color-mix(in srgb, var(--color-foreground) 12%, transparent);
  }
  .card.active {
    border-left-color: var(--task-color, var(--color-accent));
    background: color-mix(in srgb, var(--color-foreground) 14%, transparent);
  }
  .card-inner {
    display: flex; align-items: flex-start; gap: 10px;
    padding: 10px 12px;
  }
  .circle {
    width: 24px; height: 24px; border-radius: 50%; margin-top: 1px;
    border: 2px solid color-mix(in srgb, var(--color-foreground) 40%, transparent);
    background: none; cursor: pointer; flex-shrink: 0; padding: 0;
    transition: border-color 0.12s;
  }
  .circle:hover { border-color: var(--color-accent); }
  .card-content { flex: 1; min-width: 0; }
  .card-top { display: flex; align-items: baseline; gap: 8px; }
  .task-name {
    flex: 1; font-size: 15px; font-weight: 500; color: var(--color-foreground);
    overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  }
  .pomo-count {
    font-size: 13px; color: color-mix(in srgb, var(--color-foreground) 60%, transparent);
    flex-shrink: 0;
  }
  .pomo-count.over { color: var(--color-accent); font-weight: 600; }
  .pomo-dots {
    display: flex; gap: 2px; margin-top: 4px; align-items: center;
    overflow-x: auto; overflow-y: hidden;
    scrollbar-width: none; -ms-overflow-style: none;
    max-width: 100%;
  }
  .pomo-dots::-webkit-scrollbar { display: none; }
  .tomato { font-size: 10px; line-height: 1; flex-shrink: 0; }
  .tomato.empty { opacity: 0.25; filter: grayscale(1); }
  .over-label {
    font-size: 11px; font-weight: 600;
    color: var(--color-accent); margin-left: 2px;
  }

  .add-row {
    display: flex; align-items: center; gap: 8px;
    border: 2px dashed color-mix(in srgb, var(--color-foreground) 30%, transparent);
    border-radius: 8px; padding: 8px 12px; margin-top: 2px;
    transition: border-color 0.12s; overflow: hidden;
  }
  .add-row:focus-within {
    border-color: color-mix(in srgb, var(--color-foreground) 50%, transparent);
  }
  .add-input {
    flex: 1; min-width: 0; background: none; border: none; padding: 0;
    color: var(--color-foreground); font-size: 14px; outline: none;
    font-weight: 400;
  }
  .add-input::placeholder {
    color: color-mix(in srgb, var(--color-foreground) 40%, transparent);
  }
  .pomo-picker {
    display: flex; align-items: center; gap: 2px; flex-shrink: 0;
  }
  .pomo-btn {
    background: none; border: none;
    color: var(--color-foreground-darker); font-size: 12px;
    cursor: pointer; padding: 2px 4px;
    transition: color 0.1s; opacity: 0.5;
  }
  .pomo-btn:hover { color: var(--color-accent); opacity: 1; }
  .pomo-display {
    font-size: 12px; line-height: 1; white-space: nowrap; min-width: 24px; text-align: center;
  }

  .footer {
    text-align: center; font-size: 12px;
    color: color-mix(in srgb, var(--color-foreground) 60%, transparent);
    padding: 8px 0 2px;
  }
  .footer strong { color: var(--color-foreground); }
</style>
