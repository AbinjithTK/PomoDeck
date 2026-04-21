<script lang="ts">
  import { settings } from '$lib/stores/settings';
  import { applyTheme } from '$lib/stores/theme';
  import { getThemes, setSetting } from '$lib/ipc';

  let isDark = $derived($settings.theme_mode !== 'light');

  async function setMode(mode: 'dark' | 'light') {
    // Save setting — returns updated settings
    const updated = await setSetting('theme_mode', mode);
    // Apply the correct theme immediately
    const themes = await getThemes();
    const themeName = mode === 'light' ? updated.theme_light : updated.theme_dark;
    const t = themes.find(th => th.name === themeName);
    if (t) applyTheme(t);
  }
</script>

<div class="section">
  <div class="group-label">COLOR THEME</div>

  <div class="theme-cards">
    <div class="theme-card" class:active={!isDark}
      role="button" tabindex="0"
      onclick={() => setMode('light')}
      onkeydown={(e) => { if (e.key === 'Enter') setMode('light'); }}>
      <div class="preview light-preview">
        <div class="bar accent"></div>
        <div class="bar dim"></div>
      </div>
      <div class="card-footer">
        <span class="check" class:checked={!isDark}></span>
        <span class="card-label">Light</span>
      </div>
    </div>

    <div class="theme-card" class:active={isDark}
      role="button" tabindex="0"
      onclick={() => setMode('dark')}
      onkeydown={(e) => { if (e.key === 'Enter') setMode('dark'); }}>
      <div class="preview dark-preview">
        <div class="bar accent"></div>
        <div class="bar dim"></div>
      </div>
      <div class="card-footer">
        <span class="check" class:checked={isDark}></span>
        <span class="card-label">Dark</span>
      </div>
    </div>
  </div>
</div>

<style>
  .section { padding: 20px; }
  .group-label {
    font-size: 12px; font-weight: 600; letter-spacing: 0.08em;
    color: var(--color-foreground-darker); margin-bottom: 14px;
  }
  .theme-cards { display: flex; gap: 12px; }
  .theme-card {
    flex: 1; border-radius: 10px; overflow: hidden; cursor: pointer;
    border: 2px solid transparent; background: var(--color-background-light);
    transition: border-color 0.15s;
  }
  .theme-card.active { border-color: var(--color-accent); }
  .preview {
    height: 80px; padding: 18px 16px;
    display: flex; flex-direction: column; gap: 8px; justify-content: center;
  }
  .dark-preview { background: #1a1d23; }
  .light-preview { background: #f5f5f7; }
  .bar { height: 6px; border-radius: 3px; }
  .dark-preview .accent { background: #00D4AA; width: 75%; }
  .dark-preview .dim { background: #3a3f4a; width: 50%; }
  .light-preview .accent { background: #6C5CE7; width: 75%; }
  .light-preview .dim { background: #d1d1d6; width: 50%; }
  .card-footer {
    display: flex; align-items: center; gap: 8px; padding: 10px 12px;
  }
  .check {
    width: 18px; height: 18px; border-radius: 50%; flex-shrink: 0;
    border: 2px solid var(--color-foreground-darker);
    transition: background 0.15s, border-color 0.15s;
  }
  .check.checked { background: var(--color-accent); border-color: var(--color-accent); }
  .card-label { font-size: 12px; color: var(--color-foreground); }
</style>
