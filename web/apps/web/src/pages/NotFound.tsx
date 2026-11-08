import { paths } from "../router";

export default function NotFound() {
  return (
    <div class="space-y-2">
      <h1 class="text-xl font-semibold">Not found</h1>
      <a class="underline" href={paths()}>
        Back home
      </a>
    </div>
  );
}
