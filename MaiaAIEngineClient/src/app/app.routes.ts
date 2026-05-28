import { Routes } from '@angular/router';
import { ShellComponent } from './layout/shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      { path: 'dashboard',    loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent) },
      { path: 'failures',     loadComponent: () => import('./features/failures/failures-list.component').then(m => m.FailuresListComponent) },
      // Legacy detail URL — redirect to the drawer-driven list. Preserves any
      // bookmark or shared link operators have to /failures/123.
      { path: 'failures/:id', redirectTo: ({ params }) => `/failures?selected=${params['id']}` },
      { path: 'recommendations', loadComponent: () => import('./features/recommendations/recommendations.component').then(m => m.RecommendationsComponent) },
      { path: 'operator-actions', loadComponent: () => import('./features/recommendations/recommendations.component').then(m => m.RecommendationsComponent) },
      { path: 'scan-jobs',    loadComponent: () => import('./features/scan-jobs/scan-jobs.component').then(m => m.ScanJobsComponent) },
      { path: 'config/monitored-jobs', loadComponent: () => import('./features/config/monitored-jobs/monitored-jobs.component').then(m => m.MonitoredJobsComponent) },
      { path: 'config/classification-rules', loadComponent: () => import('./features/config/classification-rules/classification-rules.component').then(m => m.ClassificationRulesComponent) },
      { path: 'config/error-types',          loadComponent: () => import('./features/config/error-types/error-types.component').then(m => m.ErrorTypesComponent) },
    ]
  },
  { path: '**', redirectTo: '' }
];
