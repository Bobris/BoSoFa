import { createRouter, int } from "@solidjs/router";
import { lazy } from "solid-js";

export const Router = createRouter({
  routes: [
    { path: "/", component: lazy(() => import("./pages/NewSession")) },
    { path: "/login", component: lazy(() => import("./pages/Login")) },
    { path: "/enroll", component: lazy(() => import("./pages/Enroll")) },
    { path: "/install", component: lazy(() => import("./pages/Install")) },
    { path: "/catalog", component: lazy(() => import("./pages/Catalog")) },
    { path: "/desktop/authorize", component: lazy(() => import("./pages/DesktopAuthorize")) },
    { path: "/audit", component: lazy(() => import("./pages/Audit")) },
    { path: "/tokens", component: lazy(() => import("./pages/Tokens")) },
    { path: "/accounts", component: lazy(() => import("./pages/Accounts")) },
    { path: "/secrets", component: lazy(() => import("./pages/Secrets")) },
    { path: "/machines", component: lazy(() => import("./pages/Machines")) },
    { path: "/machines/:id", matchFilters: { id: int }, component: lazy(() => import("./pages/Machine")) },
    { path: "/people", component: lazy(() => import("./pages/People")) },
    { path: "/terminals/new", component: lazy(() => import("./pages/NewTerminal")) },
    { path: "/terminals/:machine/:terminal", component: lazy(() => import("./pages/Terminal")) },
    { path: "/sessions/new", component: lazy(() => import("./pages/NewSession")) },
    { path: "/sessions/:id", matchFilters: { id: int }, component: lazy(() => import("./pages/Session")) },
    { path: "*404", component: lazy(() => import("./pages/NotFound")) },
  ],
});

export const { paths } = Router;
