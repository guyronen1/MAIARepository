// Production build config (swapped in for environment.ts via angular.json
// fileReplacements on the `production` configuration).
//
// apiUrl is a RELATIVE path so a single production bundle works on any host
// name with no rebuild. Deployment layout: Maia.API and Maia.Client are
// sibling IIS applications under the same "Maia" site (not the site-root +
// /app layout originally planned) — so calls resolve to
// https://<host>/Maia.API/api/... — same origin, keeping the SameSite=Strict
// session cookie working and needing no CORS.
export const environment = {
  production: true,
  apiUrl: '/Maia.API/api',
  appName: 'MAIA',
  version: '1.0.0',
  dashboardRefreshIntervalMs: 5000,
  // Knowledge assistant (KnowledgeAgent Api) base URL, e.g. 'https://<host>/KnowledgeAgent'. Empty = feature off.
  // That Api serves /assistant/main.js + styles.css and must list MAIA's origin in Cors:Origins.
  knowledgeAssistantUrl: '',
};
