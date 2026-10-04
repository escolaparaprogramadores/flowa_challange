import { describe, expect, it } from 'vitest';
import { formatarInstanteNoHorarioDeBrasilia } from './dataDeBrasilia';

describe('formatarInstanteNoHorarioDeBrasilia', () => {
  it('CA-36: 23:30 UTC aparece no dia anterior, às 20:30 de Brasília', () => {
    expect(formatarInstanteNoHorarioDeBrasilia('2026-10-04T23:30:00Z')).toEqual({ diaMesAno: '04/10/2026', horaMinuto: '20:30' });
  });

  it('CA-36: 01:15 UTC do dia 5 ainda é dia 4 em Brasília, às 22:15', () => {
    expect(formatarInstanteNoHorarioDeBrasilia('2026-10-05T01:15:00Z')).toEqual({ diaMesAno: '04/10/2026', horaMinuto: '22:15' });
  });

  it('CA-36: 03:00 UTC é a meia-noite de Brasília, já no dia novo', () => {
    expect(formatarInstanteNoHorarioDeBrasilia('2026-10-05T03:00:00Z')).toEqual({ diaMesAno: '05/10/2026', horaMinuto: '00:00' });
  });

  it('CA-36: a virada de ano em UTC continua no ano anterior em Brasília', () => {
    expect(formatarInstanteNoHorarioDeBrasilia('2027-01-01T02:59:00Z')).toEqual({ diaMesAno: '31/12/2026', horaMinuto: '23:59' });
  });

  it('ASSUMI-08: aceita o receivedAt real do servidor, com microssegundos', () => {
    expect(formatarInstanteNoHorarioDeBrasilia('2026-10-04T09:34:23.390427Z')).toEqual({ diaMesAno: '04/10/2026', horaMinuto: '06:34' });
  });

  it('receivedAt fora do formato vira "—", sem quebrar a lista', () => {
    expect(formatarInstanteNoHorarioDeBrasilia('não é um instante')).toEqual({ diaMesAno: '—', horaMinuto: '' });
  });
});
