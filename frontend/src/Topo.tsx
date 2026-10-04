export function Topo() {
  return (
    <header className="topo">
      <LogoBase />
    </header>
  );
}

function LogoBase() {
  return (
    <span className="logo" role="img" aria-label="Base investimentos">
      <span className="logo-base" aria-hidden="true">base</span>
      <span className="logo-complemento" aria-hidden="true">investimentos</span>
    </span>
  );
}
