<script lang="ts">
  import { onMount } from 'svelte';
  import type { WeekJarEntry } from '$lib/ipc';

  let { entries, dates, barW = 52, barGap = 16 }: {
    entries: WeekJarEntry[]; dates: string[];
    barW?: number; barGap?: number;
  } = $props();

  const JAR_H = 48;
  const STEP = $derived(barW + barGap);
  const TOTAL_W = $derived(7 * STEP - barGap);

  const byDate = $derived.by(() => {
    const map = new Map<string, WeekJarEntry[]>();
    for (const d of dates) map.set(d, []);
    for (const e of entries) { const a = map.get(e.date); if (a) a.push(e); }
    return map;
  });

  function calcValue(items: WeekJarEntry[]): number {
    let sum = 0;
    for (const e of items) {
      sum += Math.min(e.duration_secs / 1500, 2) * (1 + e.flow_score / 100);
    }
    return Math.round(sum * 10) / 10;
  }
  const totalValue = $derived(calcValue(entries));

  interface Ball { x: number; y: number; r: number; vx: number; vy: number; color: string; frozen: boolean; }

  let canvasEls: HTMLCanvasElement[] = $state([]);
  let animId = 0;
  let allBalls: Ball[][] = [];
  let frames = 0;

  function buildBalls(items: WeekJarEntry[], w: number): Ball[] {
    return items.map((e, i) => {
      const durNorm = Math.sqrt(Math.min(e.duration_secs, 7200) / 7200);
      const r = 2.5 + durNorm * 4;
      const hue = (1 - Math.min(e.flow_score / 100, 1)) * 120;
      return { x: 6 + Math.random() * (w - 12), y: -r * 2 - i * 8, r,
        vx: (Math.random() - 0.5) * 0.3, vy: 0,
        color: `hsl(${hue}, 70%, 45%)`, frozen: false };
    });
  }

  function tick() {
    let active = false; frames++;
    const w = barW;
    for (let d = 0; d < dates.length; d++) {
      const balls = allBalls[d]; const cv = canvasEls[d];
      if (!cv || !balls || balls.length === 0) continue;
      const ctx = cv.getContext('2d'); if (!ctx) continue;
      for (const b of balls) {
        if (b.frozen) continue;
        b.vy += 0.12; b.vx *= 0.94; b.vy *= 0.94; b.x += b.vx; b.y += b.vy;
        if (b.y + b.r > JAR_H) { b.y = JAR_H - b.r; b.vy *= -0.3; b.vx *= 0.7; }
        if (b.x - b.r < 0) { b.x = b.r; b.vx *= -0.3; }
        if (b.x + b.r > w) { b.x = w - b.r; b.vx *= -0.3; }
        if (b.y - b.r < 0) { b.y = b.r; b.vy = Math.abs(b.vy) * 0.3; }
      }
      for (let p = 0; p < 8; p++) {
        for (let i = 0; i < balls.length; i++) {
          for (let j = i + 1; j < balls.length; j++) {
            const a = balls[i], bb = balls[j];
            const dx = bb.x - a.x, dy = bb.y - a.y, dSq = dx * dx + dy * dy, min = a.r + bb.r;
            if (dSq < min * min && dSq > 0.01) {
              const dist = Math.sqrt(dSq), nx = dx / dist, ny = dy / dist, ov = min - dist;
              const tr = a.r + bb.r, rA = bb.r / tr, rB = a.r / tr;
              if (!a.frozen) { a.x -= nx * ov * rA; a.y -= ny * ov * rA; }
              if (!bb.frozen) { bb.x += nx * ov * rB; bb.y += ny * ov * rB; }
            }
          }
        }
      }
      for (const b of balls) {
        if (b.frozen) continue;
        if (Math.abs(b.vx) + Math.abs(b.vy) < 0.1 && b.y + b.r >= JAR_H - 0.5) { b.vx = 0; b.vy = 0; b.frozen = true; }
        if (!b.frozen) active = true;
      }
      ctx.clearRect(0, 0, w, JAR_H);
      for (const b of balls) { ctx.beginPath(); ctx.arc(b.x, b.y, b.r, 0, Math.PI * 2); ctx.fillStyle = b.color; ctx.fill(); }
    }
    if (active && frames < 250) animId = requestAnimationFrame(tick);
  }

  onMount(() => {
    allBalls = dates.map(date => buildBalls(byDate.get(date) ?? [], barW));
    frames = 0;
    if (entries.length > 0) animId = requestAnimationFrame(tick);
    return () => { if (animId) cancelAnimationFrame(animId); };
  });
</script>

<div class="jars-row" style="width:{TOTAL_W}px">
  {#each dates as date, i}
    {@const items = byDate.get(date) ?? []}
    {@const dv = calcValue(items)}
    <div class="jar-col" style="width:{barW}px;margin-right:{i < 6 ? barGap : 0}px">
      <div class="val-row">
        {#if items.length > 0}
          <span class="coin">●</span><span class="val-text">{dv}</span>
        {:else}
          <span class="val-text dim">—</span>
        {/if}
      </div>
      <div class="mini-jar" class:empty={items.length === 0} style="width:{barW}px">
        <canvas width={barW} height={JAR_H} bind:this={canvasEls[i]}></canvas>
        <span class="jar-count">{items.length}</span>
      </div>
    </div>
  {/each}
  <div class="total-col">
    <span class="coin-lg">●</span>
    <span class="total-num">{totalValue}</span>
  </div>
</div>

<style>
  .jars-row { display: flex; align-items: flex-end; }
  .jar-col { display: flex; flex-direction: column; align-items: center; gap: 2px; flex-shrink: 0; }
  .val-row { display: flex; align-items: center; gap: 2px; height: 14px; }
  .coin { font-size: 7px; color: #d4a017; line-height: 1; }
  .val-text { font-size: 9px; font-weight: 600; color: var(--color-foreground-darker); opacity: 0.5; font-variant-numeric: tabular-nums; line-height: 1; }
  .val-text.dim { opacity: 0.2; }
  .mini-jar {
    height: 48px; position: relative; border-radius: 8px; overflow: hidden;
    background: color-mix(in oklch, var(--color-foreground) 4%, transparent);
    border: 1px solid color-mix(in oklch, var(--color-foreground) 8%, transparent);
  }
  .mini-jar.empty { opacity: 0.25; }
  .mini-jar canvas { display: block; }
  .jar-count {
    position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%);
    font-size: 22px; font-weight: 800; color: var(--color-foreground);
    opacity: 0.07; pointer-events: none; font-variant-numeric: tabular-nums;
  }
  .total-col {
    display: flex; flex-direction: column; align-items: center;
    gap: 2px; align-self: center; padding: 0 6px; margin-left: 12px;
  }
  .coin-lg { font-size: 12px; color: #d4a017; line-height: 1; }
  .total-num { font-size: 14px; font-weight: 700; color: var(--color-accent); font-variant-numeric: tabular-nums; line-height: 1; }
</style>
