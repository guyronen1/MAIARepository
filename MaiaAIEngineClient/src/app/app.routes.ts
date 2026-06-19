import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell/shell.component';
import { authGuard } from './core/guards/auth.guard';
import { adminGuard } from './core/guards/admin.guard';

export const routes: Routes = [
  // Public — no shell, no guard.
  { path: 'login', loadComponent: () => import('./features/auth/login.component').then(m => m.LoginComponent) },
  // Authenticated but shell-less (full-screen card). Reachable while must-change.
  { path: 'change-password', canActivate: [authGuard], loadComponent: () => import('./features/auth/change-password.component').then(m => m.ChangePasswordComponent) },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      { path: 'dashboard',    loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent) },
      { path: 'failures',     loadComponent: () => import('./features/failures/failures-list.component').then(m => m.FailuresListComponent) },
      // Legacy detail URL — redirect to the drawer-driven list. Preserves any
      // bookmark or shared link operators have to /failures/123.
      { path: 'failures/:id', redirectTo: ({ params }) => `/failures?selected=${params['id']}` },
      { path: 'unconfigured',  loadComponent: () => import('./features/unconfigured/unconfigured.component').then(m => m.UnconfiguredComponent) },
      { path: 'recommendations', loadComponent: () => import('./features/recommendations/recommendations.component').then(m => m.RecommendationsComponent) },
      { path: 'operator-actions', loadComponent: () => import('./features/recommendations/recommendations.component').then(m => m.RecommendationsComponent) },
      { path: 'scan-jobs',    loadComponent: () => import('./features/scan-jobs/scan-jobs.component').then(m => m.ScanJobsComponent) },
      { path: 'config/monitored-jobs', loadComponent: () => import('./features/config/monitored-jobs/monitored-jobs.component').then(m => m.MonitoredJobsComponent) },
      { path: 'config/monitored-jobs/:id', loadComponent: () => import('./features/config/monitored-jobs/job-config.component').then(m => m.JobConfigComponent) },
      { path: 'config/classification-rules', loadComponent: () => import('./features/config/classification-rules/classification-rules.component').then(m => m.ClassificationRulesComponent) },
      { path: 'config/error-types',          loadComponent: () => import('./features/config/error-types/error-types.component').then(m => m.ErrorTypesComponent) },
      { path: 'config/users', canActivate: [adminGuard], loadComponent: () => import('./features/config/users/users.component').then(m => m.UsersComponent) },
    ]
  },
  { path: '**', redirectTo: '' }
];
