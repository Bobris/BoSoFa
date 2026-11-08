import { createRouter, int } from "@solidjs/router";
import { lazy } from "solid-js";

export const Router = createRouter({
  routes: [
    { path: "/", component: lazy(() => import("./pages/Home")) },
    { path: "/login", component: lazy(() => import("./pages/Login")) },
    { path: "/enroll", component: lazy(() => import("./pages/Enroll")) },
    { path: "/catalog", component: lazy(() => import("./pages/Catalog")) },
    { path: "/accounts", component: lazy(() => import("./pages/Accounts")) },
    { path: "/sessions/new", component: lazy(() => import("./pages/NewSession")) },
    { path: "/sessions/:id", matchFilters: { id: int }, component: lazy(() => import("./pages/Session")) },
    { path: "*404", component: lazy(() => import("./pages/NotFound")) },
  ],
});

export const { paths } = Router;
