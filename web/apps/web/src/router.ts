import { createRouter } from "@solidjs/router";
import { lazy } from "solid-js";

export const Router = createRouter({
  routes: [
    { path: "/", component: lazy(() => import("./pages/Home")) },
    { path: "/login", component: lazy(() => import("./pages/Login")) },
    { path: "*404", component: lazy(() => import("./pages/NotFound")) },
  ],
});

export const { paths } = Router;
