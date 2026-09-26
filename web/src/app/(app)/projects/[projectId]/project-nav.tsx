"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { RoleBadge, StatusBadge } from "@/components/project-badges";
import type { Project } from "@/lib/types";

type NavItem = {
  label: string;
  /** Relative to the project, e.g. "/tables" ("" is the overview). */
  path: string;
  /** Whether the current page (relative to the project) belongs to this item. */
  matches: (rest: string) => boolean;
};

type NavGroup = { label: string | null; items: NavItem[] };

const groups: NavGroup[] = [
  { label: null, items: [{ label: "Overview", path: "", matches: (rest) => rest === "" }] },
  {
    label: "Design",
    items: [
      {
        label: "Tables",
        path: "/tables",
        matches: (rest) => rest.startsWith("/tables") && !rest.startsWith("/tables/diagram"),
      },
      { label: "Design with AI", path: "/assistant", matches: (rest) => rest.startsWith("/assistant") },
      { label: "Diagram", path: "/tables/diagram", matches: (rest) => rest.startsWith("/tables/diagram") },
    ],
  },
  {
    label: "Database",
    items: [
      { label: "Plan & apply", path: "/schema", matches: (rest) => rest === "/schema" },
      { label: "History", path: "/schema/history", matches: (rest) => rest.startsWith("/schema/history") },
      { label: "Data", path: "/data", matches: (rest) => rest.startsWith("/data") },
    ],
  },
  {
    label: "Export",
    items: [{ label: "API & access", path: "/api", matches: (rest) => rest.startsWith("/api") }],
  },
  { label: null, items: [{ label: "Settings", path: "/settings", matches: (rest) => rest.startsWith("/settings") }] },
];

/**
 * The project's menu: a sidebar on wide screens, a scrollable strip on phones. The current section is highlighted
 * and marked with aria-current, so every project page shares one way to move around.
 */
export function ProjectNav({ project }: { project: Project }) {
  const base = `/projects/${project.id}`;
  const pathname = usePathname();
  const rest = pathname.startsWith(base) ? pathname.slice(base.length).replace(/\/$/, "") : "";

  return (
    <aside className="grid content-start gap-4 md:sticky md:top-6 md:self-start" data-testid="project-nav">
      <div className="grid gap-2">
        <Link href="/projects" className="text-sm text-muted hover:text-foreground">
          ← All projects
        </Link>
        <p className="truncate text-lg font-semibold tracking-tight" title={project.name}>
          {project.name}
        </p>
        <div className="flex flex-wrap gap-1.5">
          <StatusBadge status={project.status} />
          <RoleBadge role={project.role} />
        </div>
      </div>

      <nav aria-label="Project">
        <ul className="-mx-4 flex gap-1 overflow-x-auto px-4 pb-1 md:mx-0 md:grid md:gap-0.5 md:overflow-visible md:px-0 md:pb-0">
          {groups.map((group, index) => (
            <li key={group.label ?? `group-${index}`} className="contents md:block">
              {group.label && (
                <p className="hidden px-3 pt-3 pb-1 text-xs font-medium tracking-wide text-muted uppercase md:block">{group.label}</p>
              )}
              <ul className="contents md:grid md:gap-0.5">
                {group.items.map((item) => {
                  const active = item.matches(rest);
                  return (
                    <li key={item.path} className="shrink-0">
                      <Link
                        href={`${base}${item.path}`}
                        aria-current={active ? "page" : undefined}
                        className={`block rounded-md px-3 py-1.5 text-sm whitespace-nowrap transition-colors ${
                          active
                            ? "bg-accent-soft font-medium text-accent"
                            : "text-muted hover:bg-surface-muted hover:text-foreground"
                        }`}
                      >
                        {item.label}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </li>
          ))}
        </ul>
      </nav>
    </aside>
  );
}
