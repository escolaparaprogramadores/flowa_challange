// A lista mostra o horário de Brasília seja qual for o fuso de quem abre a tela.
const FUSO_DE_BRASILIA = 'America/Sao_Paulo';

const formatadorDoDiaMesAnoDeBrasilia = new Intl.DateTimeFormat('pt-BR', {
  timeZone: FUSO_DE_BRASILIA,
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
});

const formatadorDaHoraMinutoDeBrasilia = new Intl.DateTimeFormat('pt-BR', {
  timeZone: FUSO_DE_BRASILIA,
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

export type InstanteNoHorarioDeBrasilia = { diaMesAno: string; horaMinuto: string };

export function formatarInstanteNoHorarioDeBrasilia(instanteEmIsoUtc: string): InstanteNoHorarioDeBrasilia {
  const instanteRecebido = new Date(instanteEmIsoUtc);
  if (Number.isNaN(instanteRecebido.getTime())) return { diaMesAno: '—', horaMinuto: '' };
  return {
    diaMesAno: formatadorDoDiaMesAnoDeBrasilia.format(instanteRecebido),
    horaMinuto: formatadorDaHoraMinutoDeBrasilia.format(instanteRecebido),
  };
}
