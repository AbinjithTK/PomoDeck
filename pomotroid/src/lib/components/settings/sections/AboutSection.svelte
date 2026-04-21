<script lang="ts">
  import { openLogDir } from '$lib/ipc';
  import * as m from '$paraglide/messages.js';
</script>

<div class="section">
  <div class="hero">
    <img src="/pomodeck-logo.png" alt="PomoDeck" class="logo" />
  </div>

  <div class="links">
    <button class="link-row" onclick={openLogDir}>
      <span>{m.about_open_log_folder()}</span>
      <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
        <path d="M1 3.5C1 2.67 1.67 2 2.5 2H5l1 1.5H9.5C10.33 3.5 11 4.17 11 5v4.5C11 10.33 10.33 11 9.5 11h-7C1.67 11 1 10.33 1 9.5V3.5Z" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
      </svg>
    </button>
  </div>

  <p class="credit">
    Built with Tauri, Svelte, and Rust.<br />
    Built with Tauri, Svelte, and Rust. MIT License.
  </p>
</div>

<style>
  .section {
    display: flex;
    flex-direction: column;
    padding: 32px 20px;
    gap: 24px;
  }

  .hero {
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 16px 0;
  }

  .logo {
    width: 180px;
    height: auto;
    object-fit: contain;
  }

  .links {
    display: flex;
    flex-direction: column;
    border: 1px solid var(--color-separator);
    border-radius: 6px;
    overflow: hidden;
  }

  .link-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px;
    background: none;
    border: none;
    cursor: pointer;
    color: var(--color-foreground);
    font-size: 0.85rem;
    letter-spacing: 0.02em;
    text-align: left;
    transition: background 0.12s;
  }

  .link-row:hover {
    background: var(--color-hover);
    color: var(--color-accent);
  }

  .credit {
    font-size: 0.72rem;
    color: var(--color-foreground-darker, var(--color-foreground));
    opacity: 0.5;
    line-height: 1.6;
  }
</style>
