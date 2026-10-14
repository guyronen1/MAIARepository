export const environment = {
  production: false,
  apiUrl: 'http://localhost:5095/api',
  appName: 'MAIA',
  version: '1.0.0',
  dashboardRefreshIntervalMs: 5000,
  // Knowledge assistant (KnowledgeAgent Api): serves the <knowledge-assistant> bundle at /assistant/* and the chat
  // API at /api/*. Empty = feature off (no launcher, no drawer). Its Cors:Origins must list this app's origin.
  knowledgeAssistantUrl: 'http://localhost:5080',
};
