<script lang="ts">
  import { onMount } from 'svelte';
  import { settings } from '$lib/stores/settings';
  import { setSetting, blockerGetSites, blockerSetSites, blockerGetApps, blockerSetApps, getInstalledApps } from '$lib/ipc';
  import type { InstalledApp } from '$lib/ipc';
  import { open as dialogOpen } from '@tauri-apps/plugin-dialog';

  let sites = $state<string[]>([]);
  let apps = $state<string[]>([]);
  let newSite = $state('');
  let newApp = $state('');
  let installedApps = $state<InstalledApp[]>([]);
  let showAppBrowser = $state(false);
  let appSearch = $state('');

  let blockingEnabled = $derived($settings.blocking_enabled);
  let filteredApps = $derived(
    installedApps.filter(a =>
      !apps.includes(a.exe_name.toLowerCase()) &&
      (appSearch === '' ||
       a.name.toLowerCase().includes(appSearch.toLowerCase()) ||
       a.exe_name.toLowerCase().includes(appSearch.toLowerCase()))
    )
  );

  async function toggleBlocking() {
    const newVal = blockingEnabled ? 'false' : 'true';
    const updated = await setSetting('blocking_enabled', newVal);
    settings.set(updated);
  }
  async function loadData() {
    try { sites = await blockerGetSites(); } catch {}
    try { apps = await blockerGetApps(); } catch {}
  }
  async function addSite() {
    const s = newSite.trim().toLowerCase(); if (!s || sites.includes(s)) return;
    sites = [...sites, s]; await blockerSetSites(sites); newSite = '';
  }
  async function removeSite(site: string) { sites = sites.filter(s => s !== site); await blockerSetSites(sites); }
  async function addApp() {
    const a = newApp.trim().toLowerCase(); if (!a || apps.includes(a)) return;
    apps = [...apps, a]; await blockerSetApps(apps); newApp = '';
  }
  async function addAppFromList(exe: string) {
    const a = exe.toLowerCase(); if (apps.includes(a)) return;
    apps = [...apps, a]; await blockerSetApps(apps);
  }
  async function removeApp(app: string) { apps = apps.filter(a => a !== app); await blockerSetApps(apps); }
  async function browseExe() {
    const path = await dialogOpen({ filters: [{ name: 'Executable', extensions: ['exe'] }] });
    if (path && typeof path === 'string') {
      const name = path.split('\\').pop()?.replace('.exe', '') ?? '';
      if (name && !apps.includes(name.toLowerCase())) {
        apps = [...apps, name.toLowerCase()]; await blockerSetApps(apps);
      }
    }
  }
  async function openAppBrowser() {
    showAppBrowser = !showAppBrowser;
    if (showAppBrowser && installedApps.length === 0) {
      try { installedApps = await getInstalledApps(); } catch {}
    }
  }
  onMount(() => { loadData(); });
</script>

<div class="section">
  <!-- Master toggle -->
  <div class="toggle-card" onclick={toggleBlocking} role="button" tabindex="0"
    onkeydown={(e) => { if (e.key === 'Enter') toggleBlocking(); }}>
    <div class="toggle-info">
      <span class="toggle-icon">🛡️</span>
      <div>
        <div class="toggle-title">Focus Blocking</div>
        <div class="toggle-desc">Block distracting websites & apps during work</div>
      </div>
    </div>
    <div class="toggle" class:on={blockingEnabled}><div class="thumb"></div></div>
  </div>

  <!-- Add website -->
  <div class="group-label">ADD WEBSITE</div>
  <div class="add-row">
    <input bind:value={newSite} placeholder="e.g. youtube.com" class="add-input"
      onkeydown={(e) => { if (e.key === 'Enter') addSite(); }} />
    <button class="add-btn" onclick={addSite}>+</button>
  </div>

  <!-- Add app -->
  <div class="group-label">ADD APP</div>
  <div class="add-row">
    <input bind:value={newApp} placeholder="e.g. discord" class="add-input"
      onkeydown={(e) => { if (e.key === 'Enter') addApp(); }} />
    <button class="add-btn" onclick={addApp}>+</button>
  </div>
  <div class="app-actions">
    <button class="action-btn" onclick={openAppBrowser}>
      {showAppBrowser ? '▾ Hide installed' : '▸ Browse installed apps'}
    </button>
    <button class="action-btn" onclick={browseExe}>📂 Browse .exe</button>
  </div>

  {#if showAppBrowser}
    <div class="app-browser">
      <input bind:value={appSearch} placeholder="Search apps..." class="search-input" />
      <div class="app-list">
        {#each filteredApps as app}
          <button class="app-item" onclick={() => addAppFromList(app.exe_name)}>
            <span class="app-name">{app.name}</span>
            <span class="app-exe">{app.exe_name}</span>
          </button>
        {/each}
        {#if filteredApps.length === 0}
          <div class="empty-msg">{appSearch ? 'No matches' : 'Loading...'}</div>
        {/if}
      </div>
    </div>
  {/if}

  <!-- Blocked list -->
  {#if sites.length > 0 || apps.length > 0}
    <div class="group-label">BLOCKED DURING FOCUS</div>
    <div class="list">
      {#each sites as site}
        <div class="list-item">
          <span class="dot web"></span>
          <span class="item-text">{site}</span>
          <button class="rm" onclick={() => removeSite(site)}>×</button>
        </div>
      {/each}
      {#each apps as app}
        <div class="list-item">
          <span class="dot app"></span>
          <span class="item-text">{app}</span>
          <button class="rm" onclick={() => removeApp(app)}>×</button>
        </div>
      {/each}
    </div>
  {:else}
    <div class="empty-state">
      <span class="empty-icon">🔓</span>
      <span>No blocked items — add websites or apps above</span>
    </div>
  {/if}
</div>

<style>
  .section { display: flex; flex-direction: column; padding: 16px 20px; gap: 8px; }

  .toggle-card {
    display: flex; align-items: center; justify-content: space-between;
    padding: 12px 14px; border-radius: 10px; background: var(--color-background-light);
    cursor: pointer; transition: background 0.12s;
  }
  .toggle-card:hover { background: var(--color-hover); }
  .toggle-info { display: flex; align-items: center; gap: 10px; }
  .toggle-icon { font-size: 18px; }
  .toggle-title { font-size: 13px; font-weight: 600; color: var(--color-foreground); }
  .toggle-desc { font-size: 11px; color: var(--color-foreground-darker); margin-top: 1px; }
  .toggle {
    width: 38px; height: 20px; border-radius: 10px; position: relative;
    background: color-mix(in oklch, var(--color-foreground) 20%, transparent);
    transition: background 0.2s; flex-shrink: 0;
  }
  .toggle.on { background: var(--color-accent); }
  .thumb {
    width: 16px; height: 16px; border-radius: 50%; background: white;
    position: absolute; top: 2px; left: 2px; transition: transform 0.2s;
    box-shadow: 0 1px 3px rgba(0,0,0,0.2);
  }
  .toggle.on .thumb { transform: translateX(18px); }

  .group-label {
    font-size: 10px; font-weight: 600; letter-spacing: 0.1em;
    color: var(--color-foreground-darker); opacity: 0.6;
    margin-top: 6px;
  }

  .add-row { display: flex; gap: 6px; }
  .add-input {
    flex: 1; background: var(--color-background-light);
    border: 1px solid color-mix(in oklch, var(--color-foreground) 12%, transparent);
    border-radius: 8px; padding: 8px 12px; color: var(--color-foreground);
    font-size: 13px; outline: none; transition: border-color 0.12s;
  }
  .add-input:focus { border-color: var(--color-accent); }
  .add-input::placeholder { color: var(--color-foreground-darker); opacity: 0.5; }
  .add-btn {
    background: var(--color-accent); color: var(--color-background); border: none;
    border-radius: 8px; width: 36px; cursor: pointer; font-size: 18px; font-weight: 500;
    display: flex; align-items: center; justify-content: center;
    transition: opacity 0.12s;
  }
  .add-btn:hover { opacity: 0.85; }

  .app-actions { display: flex; gap: 6px; }
  .action-btn {
    flex: 1; background: none;
    border: 1px dashed color-mix(in oklch, var(--color-foreground) 15%, transparent);
    border-radius: 8px; padding: 7px 10px; cursor: pointer;
    font-size: 11px; color: var(--color-foreground-darker); text-align: center;
    transition: all 0.12s;
  }
  .action-btn:hover { border-color: var(--color-accent); color: var(--color-accent); }

  .app-browser {
    border: 1px solid color-mix(in oklch, var(--color-foreground) 10%, transparent);
    border-radius: 8px; overflow: hidden;
  }
  .search-input {
    width: 100%; background: var(--color-background-light); border: none;
    border-bottom: 1px solid color-mix(in oklch, var(--color-foreground) 8%, transparent);
    padding: 8px 12px; color: var(--color-foreground); font-size: 12px; outline: none;
  }
  .search-input::placeholder { color: var(--color-foreground-darker); opacity: 0.5; }
  .app-list {
    max-height: 200px; overflow-y: auto;
    scrollbar-width: none; -ms-overflow-style: none;
  }
  .app-list::-webkit-scrollbar { display: none; }
  .app-item {
    display: flex; align-items: center; justify-content: space-between;
    width: 100%; padding: 6px 12px; background: none; border: none;
    border-bottom: 1px solid color-mix(in oklch, var(--color-foreground) 5%, transparent);
    cursor: pointer; font-size: 12px; color: var(--color-foreground); text-align: left;
  }
  .app-item:hover { background: var(--color-hover); }
  .app-item:last-child { border-bottom: none; }
  .app-name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .app-exe { font-size: 10px; color: var(--color-foreground-darker); flex-shrink: 0; margin-left: 8px; opacity: 0.6; }

  .list {
    border: 1px solid color-mix(in oklch, var(--color-foreground) 8%, transparent);
    border-radius: 8px; overflow: hidden;
  }
  .list-item {
    display: flex; align-items: center; gap: 8px; padding: 8px 12px;
    border-bottom: 1px solid color-mix(in oklch, var(--color-foreground) 5%, transparent);
    font-size: 13px; color: var(--color-foreground);
  }
  .list-item:last-child { border-bottom: none; }
  .dot {
    width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0;
  }
  .dot.web { background: #3498db; }
  .dot.app { background: #e67e22; }
  .item-text { flex: 1; }
  .rm {
    background: none; border: none; color: var(--color-foreground-darker);
    cursor: pointer; font-size: 16px; padding: 0 4px; opacity: 0.4;
    transition: all 0.12s;
  }
  .rm:hover { color: #e64137; opacity: 1; }

  .empty-msg { padding: 12px; text-align: center; font-size: 11px; color: var(--color-foreground-darker); opacity: 0.5; }
  .empty-state {
    display: flex; align-items: center; gap: 8px; justify-content: center;
    padding: 20px; color: var(--color-foreground-darker); font-size: 12px; opacity: 0.5;
  }
  .empty-icon { font-size: 16px; }
</style>
