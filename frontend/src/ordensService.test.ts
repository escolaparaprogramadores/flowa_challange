import { afterEach, describe, expect, it, vi } from 'vitest';
import { PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS, enviarOrdem, lerExposicoes, type OrdemParaEnviar } from './ordensService';

const ordemDeCompra: OrdemParaEnviar = { simbolo: 'PETR4', lado: 'Compra', quantidade: 100, precoEmCentavos: 1_050 };

function responderComJson(status: number, corpoDaResposta: unknown) {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(corpoDaResposta), { status })));
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('enviarOrdem', () => {
  it('manda o corpo no formato do contrato, com lado em inglês e preço em reais', async () => {
    const fetchDaTela = vi.fn(async () => new Response(JSON.stringify({ status: 'accepted' }), { status: 200 }));
    vi.stubGlobal('fetch', fetchDaTela);
    await enviarOrdem({ ...ordemDeCompra, lado: 'Venda' });
    const [rotaChamada, opcoesDaChamada] = fetchDaTela.mock.calls[0] as unknown as [string, RequestInit];
    expect(rotaChamada).toBe('/api/orders');
    expect(opcoesDaChamada.method).toBe('POST');
    expect(JSON.parse(String(opcoesDaChamada.body))).toEqual({ symbol: 'PETR4', side: 'sell', quantity: 100, price: 10.5 });
  });

  it('RF-25: traduz o 400 de validação do servidor nas mensagens de cada campo', async () => {
    responderComJson(400, {
      status: 'validation_error',
      message: 'A ordem tem campos inválidos.',
      errors: [{ field: 'price', message: 'O preço deve ser múltiplo de 0,01.' }],
    });
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'invalida',
      mensagemDoServidor: 'A ordem tem campos inválidos.',
      errosDeCampo: ['O preço deve ser múltiplo de 0,01.'],
    });
  });

  it('RF-23: 503 do servidor vira "ordem não confirmada", sem o nome do serviço interno', async () => {
    responderComJson(503, { status: 'communication_error', message: 'Não foi possível falar com o OrderAccumulator. Tente de novo em instantes.' });
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
    });
  });

  it('RF-23: corpo que não é JSON vira falha de comunicação com a mensagem padrão', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('<html>Bad Gateway</html>', { status: 502 })));
    expect(await enviarOrdem(ordemDeCompra)).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
    });
  });

  it('RF-23: servidor que não responde é esperado até 6 s, e não menos, e vira falha de comunicação', async () => {
    vi.useFakeTimers();
    const fetchQueNuncaResponde = (...[, opcoesDaChamada]: [string, RequestInit]) =>
      new Promise<Response>((...[, recusarChamada]: [unknown, (motivo: unknown) => void]) => {
        opcoesDaChamada.signal?.addEventListener('abort', () => recusarChamada(new DOMException('abortada', 'AbortError')));
      });
    vi.stubGlobal('fetch', vi.fn(fetchQueNuncaResponde));
    let ordemTerminou = false;
    const respostaPendente = enviarOrdem(ordemDeCompra).finally(() => { ordemTerminou = true; });
    await vi.advanceTimersByTimeAsync(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS - 1);
    expect(ordemTerminou).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(await respostaPendente).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
    });
    expect(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS).toBe(6_000);
  });

  it('RF-23: cabeçalho que chega com corpo travado também é abandonado no prazo', async () => {
    vi.useFakeTimers();
    const fetchComCorpoTravado = async (...[, opcoesDaChamada]: [string, RequestInit]) => {
      const corpoQueNuncaTermina = new ReadableStream({
        start(controleDoCorpo) {
          opcoesDaChamada.signal?.addEventListener('abort', () => controleDoCorpo.error(new DOMException('abortada', 'AbortError')));
        },
      });
      return new Response(corpoQueNuncaTermina, { status: 200 });
    };
    vi.stubGlobal('fetch', vi.fn(fetchComCorpoTravado));
    const respostaPendente = enviarOrdem(ordemDeCompra);
    await vi.advanceTimersByTimeAsync(PRAZO_MAXIMO_DE_ESPERA_DA_TELA_EM_MS);
    expect(await respostaPendente).toEqual({
      situacao: 'falha-de-comunicacao',
      mensagemDoServidor: 'A ordem não foi confirmada: o servidor de ordens não respondeu. Tente de novo em instantes.',
    });
  });
});

describe('lerExposicoes', () => {
  it('converte o corpo do contrato para a tela, na ordem recebida', async () => {
    responderComJson(200, { limit: 100_000_000, exposures: [{ symbol: 'PETR4', exposure: -500, remaining: 99_999_500 }] });
    expect(await lerExposicoes()).toEqual([{ simbolo: 'PETR4', exposicao: -500, restanteAteOLimite: 99_999_500 }]);
  });

  it('RF-32: 503 do servidor vira erro claro, sem o nome do serviço interno', async () => {
    responderComJson(503, { status: 'communication_error', message: 'Não foi possível ler a exposição no OrderAccumulator. Tente de novo em instantes.' });
    await expect(lerExposicoes()).rejects.toThrow('Não foi possível ler a exposição agora. Tente de novo em instantes.');
  });
});
