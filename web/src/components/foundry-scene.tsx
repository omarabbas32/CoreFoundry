/**
 * The foundry casting a backend: a ladle pours molten metal into four ingot molds, one per Clean Architecture
 * layer (Domain, Application, Infrastructure, API). Each glows, cools, and shows the code it became; when the last
 * one has set, the API answers a curl. Pure HTML/CSS on one timeline (no library), sized to its container
 * (container query units), and a still, finished picture for people who turn animations off.
 */

import { singular } from "@/lib/column-suggestions";

/** What the ingots and the terminal say, from one of the project's tables ("books" → Book, BookService…). */
export type CastNames = { entity: string; route: string };

export function castNames(table?: string | null): CastNames {
  const name = table && /^[a-z][a-z0-9_]*$/.test(table) ? table : "books";
  const entity = singular(name)
    .split("_")
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join("");
  return { entity, route: `/api/${name}` };
}

const layers = [
  { layer: "Domain", role: "entities" },
  { layer: "Application", role: "use cases" },
  { layer: "Infrastructure", role: "EF Core · MySQL" },
  { layer: "API", role: "controllers" },
];

// Layout, in % of the scene's width (the scene is 100cqw wide): four molds between 6% margins.
const margin = 6;
const gap = 2.7;
const column = (100 - 2 * margin - 3 * gap) / 4;
const centers = layers.map((_, index) => margin + column / 2 + index * (column + gap));
const ladleWidth = 11.5;
const spout = 0.67; // where the stream leaves the ladle, as a share of its width

// Timeline, in % of one loop: mold i is poured from pours[i] for 10%, glows, and has cooled 24% later.
const pours = [4, 22, 40, 58];
const answerAt = 86;

function keyframes(prefix: string, once: boolean) {
  const end = (value: string) => (once ? value : null);
  const ladleLeft = (i: number) => `${(centers[i] - ladleWidth * spout).toFixed(2)}cqw`;
  const ladle = [
    `0% { left: ${ladleLeft(0)}; opacity: 0; }`,
    `2% { opacity: 1; }`,
    ...pours.flatMap((p, i) => [`${p - 1}% { left: ${ladleLeft(i)}; }`, `${p + 11}% { left: ${ladleLeft(i)}; }`]),
    `72% { left: ${ladleLeft(3)}; opacity: 1; }`,
    `78% { left: calc(${ladleLeft(3)} + 12cqw); opacity: 0; }`,
    `100% { left: calc(${ladleLeft(3)} + 12cqw); opacity: 0; }`,
  ].join(" ");
  const stream = [
    `0% { transform: scaleY(0); }`,
    ...pours.flatMap((p) => [`${p}% { transform: scaleY(0); }`, `${p + 1}% { transform: scaleY(1); }`, `${p + 9}% { transform: scaleY(1); }`, `${p + 10}% { transform: scaleY(0); }`]),
    `100% { transform: scaleY(0); }`,
  ].join(" ");

  const perMold = pours
    .map((p, i) => {
      const hot = "filter: brightness(1.45) saturate(1.1);";
      const cold = "filter: brightness(.8) saturate(.85);";
      return `
      @keyframes ${prefix}-metal-${i} {
        0%, ${p}% { transform: scaleY(0); opacity: 1; ${hot} }
        ${p + 10}% { transform: scaleY(1); ${hot} }
        ${p + 14}% { ${hot} }
        ${p + 24}%, 96% { transform: scaleY(1); opacity: 1; ${cold} }
        100% { transform: scaleY(1); opacity: ${end("1") ?? "0"}; ${cold} }
      }
      @keyframes ${prefix}-halo-${i} {
        0%, ${p}% { opacity: 0; }
        ${p + 3}%, ${p + 13}% { opacity: 1; }
        ${p + 24}%, 100% { opacity: 0; }
      }
      @keyframes ${prefix}-code-${i} {
        0%, ${p + 15}% { opacity: 0; transform: translateY(4%); }
        ${p + 23}%, 96% { opacity: 1; transform: none; }
        100% { opacity: ${end("1") ?? "0"}; }
      }
      @keyframes ${prefix}-sparks-${i} {
        0%, ${p}% { opacity: 0; }
        ${p + 1}%, ${p + 9}% { opacity: 1; }
        ${p + 11}%, 100% { opacity: 0; }
      }`;
    })
    .join("\n");

  return `
    @keyframes ${prefix}-ladle { ${ladle} }
    @keyframes ${prefix}-stream { ${stream} }
    ${perMold}
    @keyframes ${prefix}-answer {
      0%, ${answerAt}% { opacity: 0; transform: translateY(6%); }
      ${answerAt + 3}%, 97% { opacity: 1; transform: none; }
      100% { opacity: ${end("1") ?? "0"}; }
    }`;
}

const styles = (prefix: string, once: boolean, seconds: number) => `
  ${keyframes(prefix, once)}
  .${prefix} { --run: ${seconds}s ${once ? "1 forwards" : "infinite"}; }
  .${prefix} .cf-ladle { animation: ${prefix}-ladle var(--run) ease-in-out; }
  .${prefix} .cf-stream { animation: ${prefix}-stream var(--run) linear; }
  .${prefix} .cf-terminal { animation: ${prefix}-answer var(--run) ease-out; }
  ${pours
    .map(
      (_, i) => `
  .${prefix} .cf-mold-${i} .cf-metal { animation: ${prefix}-metal-${i} var(--run) ease-out; }
  .${prefix} .cf-mold-${i} .cf-halo { animation: ${prefix}-halo-${i} var(--run) ease-out; }
  .${prefix} .cf-mold-${i} .cf-code { animation: ${prefix}-code-${i} var(--run) ease-in; }
  .${prefix} .cf-mold-${i} .cf-sparks { animation: ${prefix}-sparks-${i} var(--run) linear; }`,
    )
    .join("")}
  @media (prefers-reduced-motion: reduce) {
    .${prefix} .cf-ladle, .${prefix} .cf-sparks, .${prefix} .cf-halo { display: none; }
    .${prefix} .cf-metal { animation: none !important; transform: none !important; filter: brightness(.8) saturate(.85); }
    .${prefix} .cf-code, .${prefix} .cf-terminal { animation: none !important; opacity: 1 !important; transform: none !important; }
  }`;

/** Shared, static styles: drawing, not timing. */
const base = `
  .cf-scene { position: relative; width: 100%; container-type: inline-size; aspect-ratio: 960 / 470; overflow: hidden;
    border-radius: 14px; background: radial-gradient(ellipse at 50% 105%, #3a1c08 0%, #0d1117 62%); color: #e6edf3; isolation: isolate; }
  .cf-bg { position: absolute; inset: 0; padding: 1.5cqw 2cqw; font: 1.2cqw/1.9cqw ui-monospace, "Cascadia Code", Consolas, monospace;
    color: #4493f8; opacity: .11; white-space: pre; overflow: hidden; mask-image: linear-gradient(#000 40%, #0000); }
  .cf-ladle { position: absolute; top: 3cqw; width: ${ladleWidth}cqw; height: 7.3cqw; z-index: 2; }
  .cf-bowl { position: absolute; inset: 0; border-radius: .7cqw .7cqw 4cqw 4cqw; transform: rotate(-14deg);
    background: linear-gradient(#434a54, #1c2026); border: .2cqw solid #5b636e; }
  .cf-bowl::after { content: ""; position: absolute; left: .9cqw; right: .9cqw; top: .6cqw; height: 1.3cqw; border-radius: 50%;
    background: radial-gradient(#fff6c2, #ffb640 60%, #ff7b39); box-shadow: 0 0 2cqw #ff9d2e; }
  .cf-handle { position: absolute; top: 1.5cqw; left: -7.5cqw; width: 8.5cqw; height: .65cqw; border-radius: .4cqw;
    background: #5b636e; transform: rotate(-14deg); }
  .cf-stream { position: absolute; top: 6.8cqw; left: calc(${spout * 100}% - .4cqw); width: .85cqw; height: 15.6cqw; border-radius: .5cqw;
    transform-origin: top; transform: scaleY(0);
    background: linear-gradient(#fff6c2, #ffb640 35%, #ff7b39); box-shadow: 0 0 1.4cqw #ff9d2e, 0 0 3cqw #ff7b3988; }
  .cf-molds { position: absolute; left: ${margin}cqw; right: ${margin}cqw; top: 25cqw; display: grid;
    grid-template-columns: repeat(4, 1fr); gap: ${gap}cqw; }
  .cf-cast { position: relative; }
  .cf-tray { position: relative; height: 12.5cqw; transform: perspective(60cqw) rotateX(26deg); transform-origin: 50% 0; }
  .cf-mold { position: absolute; inset: 0; border-radius: 1cqw; overflow: hidden; background: #1b1511;
    border: .85cqw solid #3b2f27; border-top-color: #4b3c31; box-shadow: inset 0 .9cqw 1.9cqw #000c, 0 1.5cqw 3cqw #000a; }
  .cf-halo { position: absolute; inset: -1cqw; border-radius: 2cqw; opacity: 0; pointer-events: none;
    box-shadow: 0 0 4cqw #ff9d2eaa, 0 0 9cqw #ff7b3960; }
  .cf-metal { position: absolute; inset: 0; transform-origin: bottom; transform: scaleY(0);
    background: radial-gradient(ellipse at 40% 35%, #fff6c2, #ffcf5a 35%, #ff9a2e 70%, #d9601a); }
  .cf-metal::after { content: ""; position: absolute; inset: 0; opacity: .45; mix-blend-mode: overlay;
    background: repeating-radial-gradient(circle at 30% 60%, #fff0 0 .6cqw, #fff8 .7cqw, #fff0 .9cqw); }
  .cf-code { position: absolute; inset: 0; display: grid; place-content: center; gap: .4cqw; padding: .8cqw; text-align: center; opacity: 0;
    font: 700 1.65cqw/1.2 ui-monospace, "Cascadia Code", Consolas, monospace; color: #3d2406;
    text-shadow: 0 .1cqw 0 #ffe9a088, 0 -.1cqw .1cqw #0006; overflow-wrap: anywhere; }
  .cf-code small { font-size: 1.15cqw; font-weight: 500; opacity: .85; }
  .cf-sparks { position: absolute; left: 50%; top: -.6cqw; width: 0; height: 0; opacity: 0; z-index: 3; }
  .cf-sparks i { position: absolute; width: .45cqw; height: .45cqw; border-radius: 50%; background: #ffe08a;
    box-shadow: 0 0 .8cqw #ffb640; animation: cf-spark .7s ease-out infinite; }
  .cf-sparks i:nth-child(2) { --dx: -3cqw; animation-delay: .15s; } .cf-sparks i:nth-child(3) { --dx: 2.6cqw; animation-delay: .3s; }
  .cf-sparks i:nth-child(4) { --dx: -1.6cqw; animation-delay: .45s; } .cf-sparks i:nth-child(5) { --dx: 3.4cqw; animation-delay: .55s; }
  @keyframes cf-spark { from { transform: translate(0, 0); opacity: 1; } to { transform: translate(var(--dx, 1.4cqw), -4.5cqw); opacity: 0; } }
  .cf-label { margin-top: 1.6cqw; text-align: center; font: 1.3cqw/1.35 system-ui, sans-serif; color: #9198a1; }
  .cf-label b { display: block; font-size: 1.6cqw; color: #e6edf3; }
  .cf-terminal { position: absolute; top: 3cqw; right: ${margin}cqw; width: 40cqw; padding: 1.2cqw 1.5cqw; border-radius: .9cqw; opacity: 0; z-index: 4;
    background: #151b23ee; border: .12cqw solid #30363d; box-shadow: 0 1cqw 3cqw #0008;
    font: 1.35cqw/1.65 ui-monospace, "Cascadia Code", Consolas, monospace; }
  .cf-terminal .ok { color: #3fb950; } .cf-terminal .k { color: #79c0ff; } .cf-terminal .s { color: #ffa657; } .cf-terminal .d { color: #9198a1; }
  @media (prefers-reduced-motion: reduce) { .cf-sparks i { animation: none; } }
`;

/**
 * @param once Play the cast once and stay on the finished ingots and the answer (e.g. after an export); loops otherwise.
 */
export function FoundryScene({ names = castNames(), once = false, className }: { names?: CastNames; once?: boolean; className?: string }) {
  const prefix = once ? "cf-once" : "cf-loop";
  const { entity, route } = names;
  const code = [
    [entity, ": IEntity"],
    [`${entity}Service`, `CrudService<${entity}>`],
    [`EfRepository`, `<${entity}> · MySQL`],
    [`GET ${route}`, "[Authorize]"],
  ];
  const background = [
    `public sealed class ${entity} : IEntity { public long Id { get; set; } }`,
    `builder.Services.AddScoped<IRepository<${entity}>, EfRepository<${entity}>>();`,
    `[HttpGet] public Task<PagedResult<${entity}Dto>> List([FromQuery] PageRequest request, CancellationToken ct)`,
    `public sealed class ${entity}Service(IRepository<${entity}> repository) : CrudService<${entity}, ${entity}Dto, ${entity}Input>(repository)`,
    `dotnet ef migrations add InitialCreate    docker compose up --build    curl localhost:8080${route}`,
  ];

  return (
    <div
      className={`cf-scene ${prefix} ${className ?? ""}`}
      role="img"
      aria-label={`Molten metal cast into four molds: Domain, Application, Infrastructure and API. The API then answers GET ${route} with 200 OK.`}
    >
      <style>{base + styles(prefix, once, once ? 9 : 10)}</style>
      <div className="cf-bg" aria-hidden>
        {Array.from({ length: 4 }, () => background.join("\n")).join("\n")}
      </div>

      <div className="cf-ladle" aria-hidden>
        <div className="cf-handle" />
        <div className="cf-bowl" />
        <div className="cf-stream" />
      </div>

      <div className="cf-terminal" aria-hidden>
        <div>
          <span className="d">$</span> curl <span className="s">localhost:8080{route}</span>
        </div>
        <div>
          <span className="ok">200 OK</span> <span className="d">· application/json</span>
        </div>
        <div>
          {"{ "}
          <span className="k">&quot;items&quot;</span>: [ {"{ "}
          <span className="k">&quot;id&quot;</span>: 1, … {"} "}], <span className="k">&quot;total&quot;</span>: 1 {"}"}
        </div>
      </div>

      <div className="cf-molds" aria-hidden>
        {layers.map(({ layer, role }, index) => (
          <div key={layer} className={`cf-cast cf-mold-${index}`}>
            <div className="cf-tray">
              <div className="cf-halo" />
              <div className="cf-mold">
                <div className="cf-metal" />
                <div className="cf-code">
                  <span>{code[index][0]}</span>
                  <small>{code[index][1]}</small>
                </div>
              </div>
              <div className="cf-sparks">
                <i />
                <i />
                <i />
                <i />
                <i />
              </div>
            </div>
            <div className="cf-label">
              <b>{layer}</b>
              {role}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

/** A tiny pour for inline "working…" states: a drop falls into a mold that fills, glows and cools, on repeat. */
export function FoundryPour({ size = 22 }: { size?: number }) {
  return (
    <span aria-hidden className="cf-pour" style={{ width: size, height: size }}>
      <style>{`
        .cf-pour { position: relative; display: inline-block; flex: none; }
        .cf-pour .drop { position: absolute; left: 50%; top: 0; width: 26%; height: 26%; margin-left: -13%; border-radius: 50% 50% 50% 0;
          transform: rotate(-45deg); background: #ffb640; box-shadow: 0 0 4px #ff9d2e; animation: cf-drop 1.6s ease-in infinite; }
        .cf-pour .tray { position: absolute; left: 8%; right: 8%; bottom: 4%; height: 42%; border: 2px solid currentColor; border-top: 0;
          border-radius: 0 0 5px 5px; overflow: hidden; opacity: .8; }
        .cf-pour .fill { position: absolute; inset: 0; transform-origin: bottom;
          background: linear-gradient(#ffe08a, #ff7b39); animation: cf-fill 1.6s ease-out infinite; }
        @keyframes cf-drop { 0% { top: 0; opacity: 0; } 15% { opacity: 1; } 45% { top: 44%; opacity: 1; } 50%, 100% { top: 44%; opacity: 0; } }
        @keyframes cf-fill { 0%, 45% { transform: scaleY(.15); filter: brightness(1.3); } 70% { transform: scaleY(.9); filter: brightness(1.3); }
          100% { transform: scaleY(.9); filter: brightness(.8); } }
        @media (prefers-reduced-motion: reduce) { .cf-pour .drop { display: none; } .cf-pour .fill { animation: none; transform: scaleY(.9); } }
      `}</style>
      <span className="drop" />
      <span className="tray">
        <span className="fill" />
      </span>
    </span>
  );
}
