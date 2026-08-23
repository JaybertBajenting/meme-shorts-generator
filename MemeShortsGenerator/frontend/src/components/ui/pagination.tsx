import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";

/** Compact "Prev / page X of N / Next" pager. Renders nothing for a single page. */
export function Pagination({
  page,
  pageCount,
  onPage,
}: {
  page: number;
  pageCount: number;
  onPage: (p: number) => void;
}) {
  if (pageCount <= 1) return null;
  return (
    <div className="flex items-center justify-center gap-2 pt-2">
      <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        <ChevronLeft className="size-4" /> Prev
      </Button>
      <span className="text-xs text-muted-foreground">
        Page {page} of {pageCount}
      </span>
      <Button variant="outline" size="sm" disabled={page >= pageCount} onClick={() => onPage(page + 1)}>
        Next <ChevronRight className="size-4" />
      </Button>
    </div>
  );
}

/** Clamp a page number and compute total pages + the current slice. */
export function paginate<T>(items: T[], page: number, perPage: number) {
  const pageCount = Math.max(1, Math.ceil(items.length / perPage));
  const safePage = Math.min(Math.max(1, page), pageCount);
  const slice = items.slice((safePage - 1) * perPage, safePage * perPage);
  return { slice, pageCount, safePage };
}
