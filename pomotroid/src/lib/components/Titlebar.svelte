<script lang="ts">
  import { onMount } from 'svelte';
  import { getCurrentWebviewWindow, WebviewWindow } from '@tauri-apps/api/webviewWindow';
  import { setWindowVisibility, flowGetState, onFlowChanged, blockerGetJar, getStreak } from '$lib/ipc';
  import type { FlowState, JarEntry, StreakInfo } from '$lib/ipc';
  import { settings } from '$lib/stores/settings';
  import { isMac } from '$lib/utils/platform';
  import Tooltip from './Tooltip.svelte';
  import * as m from '$paraglide/messages.js';

  let maximized = $state(false);
  let flow = $state<FlowState | null>(null);
  let showStreakPopup = $state(false);
  let jarEntries = $state<JarEntry[]>([]);
  let streak = $state<StreakInfo>({ current: 0, longest: 0 });
  let jarCanvas: HTMLCanvasElement | undefined = $state();

  // Jar physics
  interface JarBall { x: number; y: number; r: number; vx: number; vy: number; color: string; frozen: boolean; }
  let jarBalls: JarBall[] = [];
  let jarAnimId = 0;
  const JAR_W = 320; const JAR_H = 180;

  function startJarPhysics() {
    if (jarAnimId) cancelAnimationFrame(jarAnimId);
    jarBalls = jarEntries.map((e, i) => {
      const durNorm = Math.sqrt(Math.min(e.duration_secs, 7200) / 7200);
      const r = 4 + durNorm * 8;
      const hue = (1 - Math.min(e.flow_score / 100, 1)) * 120;
      return {
        x: 20 + Math.random() * (JAR_W - 40),
        y: -r * 2 - i * 12 - Math.random() * 30,
        r, vx: (Math.random() - 0.5) * 0.8, vy: 0,
        color: `hsl(${hue}, 72%, 46%)`, frozen: false,
      };
    });
    let frames = 0;
    function tick() {
      if (!jarCanvas) return;
      const ctx = jarCanvas.getContext('2d');
      if (!ctx) return;
      frames++;
      let active = false;
      for (const b of jarBalls) {
        if (b.frozen) continue;
        b.vy += 0.18; b.vx *= 0.95; b.vy *= 0.95;
        b.x += b.vx; b.y += b.vy;
        if (b.y + b.r > JAR_H) { b.y = JAR_H - b.r; b.vy *= -0.35; b.vx *= 0.7; }
        if (b.x - b.r < 0) { b.x = b.r; b.vx *= -0.3; }
        if (b.x + b.r > JAR_W) { b.x = JAR_W - b.r; b.vx *= -0.3; }
        if (b.y - b.r < 0) { b.y = b.r; b.vy = Math.abs(b.vy) * 0.2; }
      }
      for (let p = 0; p < 8; p++) {
        for (let i = 0; i < jarBalls.length; i++) {
          for (let j = i + 1; j < jarBalls.length; j++) {
            const a = jarBalls[i], bb = jarBalls[j];
            const dx = bb.x - a.x, dy = bb.y - a.y;
            const dSq = dx * dx + dy * dy, min = a.r + bb.r;
            if (dSq < min * min && dSq > 0.01) {
              const d = Math.sqrt(dSq), nx = dx / d, ny = dy / d, ov = min - d;
              const tr = a.r + bb.r, rA = bb.r / tr, rB = a.r / tr;
              if (!a.frozen) { a.x -= nx * ov * rA; a.y -= ny * ov * rA; }
              if (!bb.frozen) { bb.x += nx * ov * rB; bb.y += ny * ov * rB; }
            }
          }
        }
      }
      for (const b of jarBalls) {
        if (b.frozen) continue;
        if (Math.abs(b.vx) + Math.abs(b.vy) < 0.12 && b.y + b.r >= JAR_H - 0.5) {
          b.vx = 0; b.vy = 0; b.frozen = true;
        }
        if (!b.frozen) active = true;
      }
      ctx.clearRect(0, 0, JAR_W, JAR_H);
      for (const b of jarBalls) {
        // Base color fill
        ctx.beginPath(); ctx.arc(b.x, b.y, b.r, 0, Math.PI * 2);
        ctx.fillStyle = b.color; ctx.fill();
        // 3D highlight — radial gradient from top-left
        const grad = ctx.createRadialGradient(b.x - b.r * 0.3, b.y - b.r * 0.3, b.r * 0.1, b.x, b.y, b.r);
        grad.addColorStop(0, 'rgba(255,255,255,0.35)');
        grad.addColorStop(0.5, 'rgba(255,255,255,0.05)');
        grad.addColorStop(1, 'rgba(0,0,0,0.15)');
        ctx.fillStyle = grad; ctx.fill();
      }
      if (active && frames < 300) jarAnimId = requestAnimationFrame(tick);
    }
    jarAnimId = requestAnimationFrame(tick);
  }

  $effect(() => {
    if (showStreakPopup && jarCanvas && jarEntries.length > 0) {
      // Small delay so canvas is mounted
      requestAnimationFrame(() => startJarPhysics());
    }
    return () => { if (jarAnimId) cancelAnimationFrame(jarAnimId); };
  });

  onMount(() => {
    const win = getCurrentWebviewWindow();
    const cleanups: (() => void)[] = [];
    win.isMaximized().then((v) => { maximized = v; });
    const unlisten = win.onResized(async () => { maximized = await win.isMaximized(); });

    (async () => {
      const { listen: listenEvent } = await import('@tauri-apps/api/event');
      cleanups.push(await listenEvent('navigate:stats', () => { openStats('week'); }));
      cleanups.push(await listenEvent('navigate:streak', () => { showStreakPopup = true; }));
      // Flow state
      try { flow = await flowGetState(); } catch {}
      cleanups.push(await onFlowChanged((f) => { flow = f; }));
      // Jar data
      try { jarEntries = await blockerGetJar(); } catch {}
      try { streak = await getStreak(); } catch {}
      // Refresh jar + streak on round change
      cleanups.push(await listenEvent('timer:round-change', async () => {
        try { jarEntries = await blockerGetJar(); } catch {}
        try { streak = await getStreak(); } catch {}
      }));
    })();

    return () => {
      unlisten.then((fn) => fn());
      cleanups.forEach(u => u());
    };
  });

  async function openSettings() {
    const existing = await WebviewWindow.getByLabel('settings');
    if (existing) {
      await existing.show();
      await existing.setFocus();
      return;
    }
    new WebviewWindow('settings', {
      url: '/settings',
      title: 'PomoDeck — Settings',
      width: 720,
      height: 520,
      decorations: isMac,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      titleBarStyle: isMac ? ('Overlay' as any) : undefined,
      hiddenTitle: isMac ? true : undefined,
      resizable: false,
      visible: false,
    });
  }

  async function openFocusSettings() {
    const existing = await WebviewWindow.getByLabel('settings');
    if (existing) {
      await existing.show();
      await existing.setFocus();
      await existing.emit('switch-section', 'focus');
      return;
    }
    new WebviewWindow('settings', {
      url: '/settings?section=focus',
      title: 'PomoDeck — Settings',
      width: 720,
      height: 520,
      decorations: isMac,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      titleBarStyle: isMac ? ('Overlay' as any) : undefined,
      hiddenTitle: isMac ? true : undefined,
      resizable: false,
      visible: false,
    });
  }

  async function openStats(tab?: string) {
    const existing = await WebviewWindow.getByLabel('stats');
    if (existing) {
      await existing.show();
      await existing.setFocus();
      if (tab) await existing.emit('switch-tab', tab);
      return;
    }
    const url = tab ? `/stats?tab=${tab}` : '/stats';
    new WebviewWindow('stats', {
      url,
      title: 'PomoDeck — Statistics',
      width: 840,
      height: 700,
      minWidth: 500,
      minHeight: 400,
      decorations: isMac,
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      titleBarStyle: isMac ? ('Overlay' as any) : undefined,
      hiddenTitle: isMac ? true : undefined,
      resizable: true,
      visible: false,
    });
  }

  async function minimize() {
    if ($settings.min_to_tray) {
      await setWindowVisibility(false);
    } else {
      await getCurrentWebviewWindow().minimize();
    }
  }

  function toggleMaximize() {
    getCurrentWebviewWindow().toggleMaximize();
  }

  function close() {
    getCurrentWebviewWindow().close();
  }
</script>

{#snippet settingsBtn()}
  <Tooltip text={m.tooltip_settings()}>
    <button class="btn-icon" onclick={openSettings} aria-label="Settings">
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
        <line
          x1="2"
          y1="4"
          x2="14"
          y2="4"
          stroke="currentColor"
          stroke-width="1.3"
          stroke-linecap="round"
        />
        <circle
          cx="5"
          cy="4"
          r="1.8"
          fill="var(--color-background)"
          stroke="currentColor"
          stroke-width="1.3"
        />
        <line
          x1="2"
          y1="8"
          x2="14"
          y2="8"
          stroke="currentColor"
          stroke-width="1.3"
          stroke-linecap="round"
        />
        <circle
          cx="11"
          cy="8"
          r="1.8"
          fill="var(--color-background)"
          stroke="currentColor"
          stroke-width="1.3"
        />
        <line
          x1="2"
          y1="12"
          x2="14"
          y2="12"
          stroke="currentColor"
          stroke-width="1.3"
          stroke-linecap="round"
        />
        <circle
          cx="7"
          cy="12"
          r="1.8"
          fill="var(--color-background)"
          stroke="currentColor"
          stroke-width="1.3"
        />
      </svg>
    </button>
  </Tooltip>
{/snippet}

{#snippet statsBtn()}
  <Tooltip text="Focus Blocking">
    <button class="btn-icon" class:active-indicator={flow?.blocking_active} onclick={openFocusSettings} aria-label="Focus Blocking">
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
        <path d="M8 1L2 4v4c0 3.5 2.5 6.5 6 7.5 3.5-1 6-4 6-7.5V4L8 1z" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" fill={flow?.blocking_active ? 'var(--color-accent)' : 'none'} fill-opacity="0.2"/>
      </svg>
    </button>
  </Tooltip>
  <Tooltip text={m.tooltip_statistics()}>
    <button class="btn-icon" onclick={openStats} aria-label="Statistics">
      <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
        <rect x="2" y="9" width="3" height="5" rx="0.5" fill="currentColor" opacity="0.6" />
        <rect x="6.5" y="5" width="3" height="9" rx="0.5" fill="currentColor" opacity="0.8" />
        <rect x="11" y="2" width="3" height="12" rx="0.5" fill="currentColor" />
      </svg>
    </button>
  </Tooltip>
  {#if true}
    <button class="btn-icon streak-btn" onclick={() => showStreakPopup = !showStreakPopup} aria-label="Streak">
      <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
        <path d="M8 1C8 1 5.5 4 5.5 6.5C5.5 7.5 6 8.5 6 8.5C6 8.5 5 8 4.5 6.5C4.5 6.5 3 8.5 3 10.5C3 13 5.2 15 8 15C10.8 15 13 13 13 10.5C13 7 8 1 8 1Z"/>
      </svg>
      {#if streak.current > 0}<span class="streak-num">{streak.current}</span>{/if}
    </button>
  {/if}
  {#if flow && flow.in_focus}
    <span class="flow-badge" style="opacity:{0.4 + (flow.score / 100) * 0.6}">{flow.score}</span>
  {/if}
{/snippet}

<nav class="titlebar" data-tauri-drag-region>
  <!-- Left: settings + stats buttons on Linux/Windows. On macOS the traffic
       lights live here; the action buttons move to the right side instead. -->
  {#if !isMac}
    {@render settingsBtn()}
    {@render statsBtn()}
  {/if}

  <!-- Right: settings + stats buttons on macOS, window controls on Linux/Windows. -->
  <div class="controls">
    {#if isMac}
      {@render statsBtn()}
      {@render settingsBtn()}
    {:else}
      <button class="btn-icon" onclick={minimize} aria-label="Minimize">
        <svg width="12" height="12" viewBox="0 0 12 12">
          <line
            x1="1"
            y1="6"
            x2="11"
            y2="6"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
          />
        </svg>
      </button>
      <button
        class="btn-icon"
        onclick={toggleMaximize}
        aria-label={maximized ? 'Restore' : 'Maximize'}
      >
        {#if maximized}
          <svg width="12" height="12" viewBox="0 0 12 12">
            <rect
              x="3"
              y="1"
              width="8"
              height="8"
              rx="1"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
            />
            <path
              d="M1 4 L1 11 L8 11"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
              stroke-linecap="round"
              stroke-linejoin="round"
            />
          </svg>
        {:else}
          <svg width="12" height="12" viewBox="0 0 12 12">
            <rect
              x="1"
              y="1"
              width="10"
              height="10"
              rx="1"
              fill="none"
              stroke="currentColor"
              stroke-width="1.5"
            />
          </svg>
        {/if}
      </button>
      <button class="btn-icon close" onclick={close} aria-label="Close">
        <svg width="12" height="12" viewBox="0 0 12 12">
          <line
            x1="1"
            y1="1"
            x2="11"
            y2="11"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
          />
          <line
            x1="11"
            y1="1"
            x2="1"
            y2="11"
            stroke="currentColor"
            stroke-width="1.5"
            stroke-linecap="round"
          />
        </svg>
      </button>
    {/if}
  </div>
</nav>

{#if showStreakPopup}
  <div class="popup-overlay" onclick={() => showStreakPopup = false} role="none"></div>
  <div class="streak-popup">
    <button class="popup-close" onclick={() => showStreakPopup = false}>×</button>
    <div class="jar-container">
      <canvas bind:this={jarCanvas} width={JAR_W} height={JAR_H} class="jar-canvas"></canvas>
      {#if jarEntries.length === 0}
        <span class="jar-empty">No pomos today</span>
      {/if}
      {#if jarEntries.length > 0}
        <span class="jar-count-watermark">{jarEntries.length}</span>
      {/if}
    </div>
    <div class="popup-stats">
      <div class="popup-stat">
        <span class="stat-val">{jarEntries.length}</span>
        <span class="stat-label">POMOS</span>
      </div>
      <div class="popup-divider"></div>
      <div class="popup-stat">
        <span class="stat-val">{Math.round(jarEntries.reduce((s, e) => s + e.duration_secs, 0) / 60)}<small>m</small></span>
        <span class="stat-label">FOCUS</span>
      </div>
      <div class="popup-divider"></div>
      <div class="popup-stat">
        <span class="stat-val">{jarEntries.length > 0 ? Math.round(jarEntries.reduce((s, e) => s + e.flow_score, 0) / jarEntries.length) : 0}<small>%</small></span>
        <span class="stat-label">FLOW</span>
      </div>
      <div class="popup-divider"></div>
      <div class="popup-stat">
        <span class="stat-val accent">{streak.current}</span>
        <span class="stat-label">STREAK</span>
      </div>
    </div>
    {#if streak.longest > 0}
      <div class="popup-best">Best: {streak.longest} days</div>
    {/if}
  </div>
{/if}

<style>
  .titlebar {
    height: 40px;
    width: 100%;
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0 8px;
    position: relative;
    flex-shrink: 0;
  }

  .controls {
    display: flex;
    gap: 4px;
    margin-left: auto;
  }

  .btn-icon {
    background: none;
    border: none;
    cursor: pointer;
    color: var(--color-foreground-darker, var(--color-foreground));
    display: flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    border-radius: 4px;
    transition:
      color 0.15s,
      background 0.15s;
  }

  .btn-icon:hover {
    color: var(--color-foreground);
    background: var(--color-hover);
  }

  .btn-icon.close:hover {
    color: var(--color-background);
    background: var(--color-focus-round);
  }

  .streak-btn {
    display: flex; align-items: center; gap: 2px;
  }
  .streak-num {
    font-size: 11px; font-weight: 600; color: var(--color-accent);
  }
  .flow-badge {
    font-size: 10px; font-weight: 600; color: var(--color-accent);
    padding: 2px 6px; border-radius: 4px; margin-left: 4px;
    background: color-mix(in srgb, var(--color-accent) 15%, transparent);
    animation: flow-glow 2s ease-in-out infinite;
  }
  @keyframes flow-glow {
    0%, 100% { box-shadow: 0 0 0 0 transparent; }
    50% { box-shadow: 0 0 6px 1px color-mix(in srgb, var(--color-accent) 25%, transparent); }
  }
  .active-indicator { color: var(--color-accent); }

  /* Streak popup */
  .popup-overlay {
    position: fixed; inset: 0; z-index: 999;
  }
  .streak-popup {
    position: fixed; top: 40px; left: 50%; transform: translateX(-50%);
    width: calc(100% - 24px); max-width: 340px;
    background: var(--color-background-light);
    border: 1px solid var(--color-separator, rgba(255,255,255,0.08));
    border-radius: 12px; z-index: 1000;
    box-shadow: 0 8px 32px rgba(0,0,0,0.4);
    overflow: hidden;
    animation: popup-in 0.15s ease;
  }
  @keyframes popup-in {
    from { opacity: 0; transform: translateX(-50%) translateY(-8px); }
    to { opacity: 1; transform: translateX(-50%) translateY(0); }
  }
  .popup-close {
    position: absolute; top: 6px; right: 8px; background: none; border: none;
    color: var(--color-foreground-darker); font-size: 18px; cursor: pointer;
    z-index: 1; padding: 2px 6px; border-radius: 4px;
  }
  .popup-close:hover { color: var(--color-foreground); }
  .jar-container {
    position: relative; height: 180px; overflow: hidden;
    background: var(--color-background);
    border-bottom: 1px solid var(--color-separator, rgba(255,255,255,0.06));
  }
  .jar-canvas { display: block; width: 100%; height: 100%; }
  .jar-count-watermark {
    position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%);
    font-size: 56px; font-weight: 800; color: var(--color-foreground);
    opacity: 0.04; pointer-events: none; font-variant-numeric: tabular-nums;
  }
  .jar-empty {
    position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%);
    font-size: 12px; color: var(--color-foreground-darker); opacity: 0.5;
  }
  .popup-stats {
    display: flex; align-items: stretch;
  }
  .popup-stat {
    flex: 1; display: flex; flex-direction: column; align-items: center;
    justify-content: center; gap: 2px; padding: 12px 8px;
  }
  .stat-val {
    font-size: 22px; font-weight: 700; color: var(--color-foreground);
    font-variant-numeric: tabular-nums; line-height: 1;
  }
  .stat-val small { font-size: 13px; font-weight: 400; }
  .stat-val.accent { color: var(--color-accent); }
  .stat-label {
    font-size: 9px; font-weight: 600; letter-spacing: 0.08em;
    color: var(--color-foreground-darker);
  }
  .popup-divider {
    width: 1px; background: var(--color-separator, rgba(255,255,255,0.06));
    margin: 8px 0;
  }
  .popup-best {
    text-align: center; font-size: 10px; color: var(--color-foreground-darker);
    padding: 4px 0 8px; opacity: 0.6;
  }
</style>
