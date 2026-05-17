import { Component } from '@angular/core';

@Component({
  selector: 'app-top-bar',
  standalone: true,
  imports: [],
  template: `
    <header class="topbar">
      <div class="topbar-left">
        <img src="ness-icon.png" alt="NESS" class="brand-logo" />
        <div class="brand-divider"></div>
        <div class="brand-text">
          <span class="brand-name">MAIA</span>
          <span class="brand-subtitle">Intelligent Monitoring & Auto-Healing Platform</span>
        </div>
      </div>

      <div class="topbar-right">
        <div class="live-badge">
          <span class="dot"></span>
          <span>Live</span>
        </div>
        <div class="user-chip">
          <div class="user-avatar">OP</div>
          <div class="user-info">
            <span class="user-name">Operator</span>
            <span class="user-role">Admin</span>
          </div>
        </div>
      </div>
    </header>
  `,
  styles: [`
    .topbar {
      height: var(--topbar-h);
      background: var(--surface);
      border-bottom: 2px solid var(--primary);
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0 20px;
      flex-shrink: 0;
      box-shadow: 0 2px 6px rgba(0,0,0,0.08);
      z-index: 100;
    }
    .topbar-left  { display: flex; align-items: center; gap: 14px; }
    .brand-logo   { height: 38px; width: 38px; object-fit: contain; border-radius: 50%; }
    .brand-divider{ width: 1px; height: 28px; background: var(--border); }
    .brand-text   { display: flex; flex-direction: column; line-height: 1.2; }
    .brand-name   { font-size: 15px; font-weight: 700; color: var(--text); letter-spacing: -0.01em; }
    .brand-subtitle { font-size: 10px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.08em; }

    .topbar-right { display: flex; align-items: center; gap: 16px; }

    .live-badge {
      display: flex; align-items: center; gap: 5px;
      font-size: 11px; font-weight: 600; color: var(--success);
      background: var(--success-bg); border: 1px solid #86efac;
      padding: 3px 10px; border-radius: 20px;
      .dot { width: 6px; height: 6px; background: var(--success); border-radius: 50%; animation: pulse 2s infinite; }
    }
    @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:0.4} }

    .user-chip {
      display: flex; align-items: center; gap: 8px;
      padding: 4px 12px 4px 4px;
      background: var(--surface-2); border: 1px solid var(--border);
      border-radius: 20px; cursor: pointer;
      transition: background var(--transition);
      &:hover { background: var(--surface-3); }
    }
    .user-avatar {
      width: 28px; height: 28px;
      background: var(--primary);
      border-radius: 50%;
      display: flex; align-items: center; justify-content: center;
      font-size: 10px; font-weight: 700; color: #fff;
    }
    .user-info    { display: flex; flex-direction: column; line-height: 1.2; }
    .user-name    { font-size: 12px; font-weight: 600; color: var(--text); }
    .user-role    { font-size: 10px; color: var(--text-muted); }
  `]
})
export class TopBarComponent {}
