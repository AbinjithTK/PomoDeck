<script lang="ts">
  import { onMount } from 'svelte';
  import { taskList, onTasksChanged } from '$lib/ipc';
  import type { TaskItem } from '$lib/ipc';

  let active = $state<TaskItem | null>(null);

  async function refresh() {
    try {
      const tasks = await taskList();
      active = tasks.find(t => t.is_active) ?? null;
    } catch {}
  }

  onMount(() => {
    const c: (() => void)[] = [];
    refresh();
    (async () => { c.push(await onTasksChanged(() => refresh())); })();
    return () => c.forEach(fn => fn());
  });
</script>

{#if active}
  <div class="active-task" style="--task-color:{active.color || 'var(--color-accent)'}">
    <span class="task-dot"></span>
    <span class="task-name">{active.title}</span>
    <span class="task-progress">{active.completed_pomodoros}/{active.estimated_pomodoros}</span>
  </div>
{/if}

<style>
  .active-task {
    display: flex; align-items: center;
    gap: 6px; padding: 4px 16px 8px; width: 100%;
    justify-content: center;
  }
  .task-dot {
    width: 8px; height: 8px; border-radius: 50%;
    background: var(--task-color); flex-shrink: 0;
  }
  .task-name {
    font-size: 13px; color: var(--color-foreground); font-weight: 600;
    max-width: 60%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  }
  .task-progress {
    font-size: 11px; color: var(--color-foreground-darker);
    font-variant-numeric: tabular-nums; flex-shrink: 0;
  }
</style>
