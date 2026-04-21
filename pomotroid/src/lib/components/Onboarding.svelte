<script lang="ts">
  import { setSetting } from '$lib/ipc';
  import { settings } from '$lib/stores/settings';

  let { onDismiss }: { onDismiss: () => void } = $props();

  async function dismiss() {
    await setSetting('onboarding_done', 'true');
    onDismiss();
  }
</script>

<div class="overlay" onclick={dismiss} role="none">
  <div class="card" onclick={(e) => e.stopPropagation()} role="dialog">
    <img src="/pomodeck-logo.png" alt="PomoDeck" class="logo" />
    <p class="tagline">Focus you can feel.</p>

    <div class="steps">
      <div class="step">
        <span class="num">1</span>
        <div>
          <strong>Install the plugin</strong>
          <span>Double-click PomoDeck.lplug4 to add actions to your MX Creative Console</span>
        </div>
      </div>
      <div class="step">
        <span class="num">2</span>
        <div>
          <strong>Assign buttons</strong>
          <span>Open Logi Options+ and drag PomoDeck actions onto your console buttons</span>
        </div>
      </div>
      <div class="step">
        <span class="num">3</span>
        <div>
          <strong>Press start</strong>
          <span>Tap the Focus Timer button on your console — or press play here</span>
        </div>
      </div>
    </div>

    <button class="start-btn" onclick={dismiss}>Get Started</button>
    <p class="hint">The app runs in your system tray. The plugin connects automatically.</p>
  </div>
</div>

<style>
  .overlay {
    position: fixed; inset: 0; z-index: 9999;
    background: rgba(0,0,0,0.6); display: flex;
    align-items: center; justify-content: center;
    animation: fade-in 0.3s ease;
  }
  @keyframes fade-in { from { opacity: 0; } to { opacity: 1; } }
  .card {
    background: var(--color-background-light);
    border: 1px solid var(--color-separator);
    border-radius: 16px; padding: 32px 28px 24px;
    max-width: 320px; width: 90%;
    display: flex; flex-direction: column; align-items: center;
    gap: 16px; box-shadow: 0 16px 48px rgba(0,0,0,0.4);
  }
  .logo { width: 140px; height: auto; }
  .tagline {
    font-size: 13px; color: var(--color-foreground-darker);
    margin: -8px 0 0; font-style: italic;
  }
  .steps { display: flex; flex-direction: column; gap: 12px; width: 100%; }
  .step {
    display: flex; gap: 12px; align-items: flex-start;
  }
  .num {
    width: 24px; height: 24px; border-radius: 50%; flex-shrink: 0;
    background: var(--color-accent); color: var(--color-background);
    font-size: 12px; font-weight: 700;
    display: flex; align-items: center; justify-content: center;
  }
  .step div { display: flex; flex-direction: column; gap: 2px; }
  .step strong { font-size: 13px; color: var(--color-foreground); }
  .step span { font-size: 11px; color: var(--color-foreground-darker); line-height: 1.4; }
  .start-btn {
    width: 100%; padding: 10px; border: none; border-radius: 8px;
    background: var(--color-accent); color: var(--color-background);
    font-size: 14px; font-weight: 600; cursor: pointer;
    transition: opacity 0.12s;
  }
  .start-btn:hover { opacity: 0.9; }
  .hint {
    font-size: 10px; color: var(--color-foreground-darker);
    opacity: 0.5; text-align: center;
  }
</style>
