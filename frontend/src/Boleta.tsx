import { useState, type FormEvent } from 'react';
import {
  QUANTIDADE_MAXIMA_EXCLUSIVA,
  SIMBOLOS_DA_BOLETA,
  formatarReais,
  lerQuantidadeInteiraDigitada,
  validarPreco,
  validarQuantidade,
  type LadoDaOrdem,
  type SimboloDaBoleta,
} from './lib/validacaoDaOrdem';
import type { OrdemParaEnviar } from './ordensService';

type PropsDaBoleta = {
  enviandoOrdem: boolean;
  aoEnviarOrdem: (ordemParaEnviar: OrdemParaEnviar) => void;
};

type ErrosDaBoleta = { erroDaQuantidade?: string; erroDoPreco?: string };

export function Boleta({ enviandoOrdem, aoEnviarOrdem }: PropsDaBoleta) {
  const [simboloDaOrdem, setSimboloDaOrdem] = useState<SimboloDaBoleta>('PETR4');
  const [ladoDaOrdem, setLadoDaOrdem] = useState<LadoDaOrdem>('Compra');
  const [quantidadeDigitada, setQuantidadeDigitada] = useState('100');
  const [precoDigitado, setPrecoDigitado] = useState('');
  const [errosDaBoleta, setErrosDaBoleta] = useState<ErrosDaBoleta>({});

  const validacaoDaQuantidade = validarQuantidade(quantidadeDigitada);
  const validacaoDoPreco = validarPreco(precoDigitado);
  const quantidadeAceita = validacaoDaQuantidade.quantidadeAceita;
  const precoAceitoEmCentavos = validacaoDoPreco.precoAceitoEmCentavos;
  const totalEstimadoEmReais =
    quantidadeAceita !== undefined && precoAceitoEmCentavos !== undefined
      ? (quantidadeAceita * precoAceitoEmCentavos) / 100
      : undefined;

  function ajustarQuantidadeEm(passoDaQuantidadeDaOrdem: number) {
    const quantidadeAtual = lerQuantidadeInteiraDigitada(quantidadeDigitada) ?? 0;
    const proximaQuantidade = Math.min(Math.max(quantidadeAtual + passoDaQuantidadeDaOrdem, 1), QUANTIDADE_MAXIMA_EXCLUSIVA - 1);
    setQuantidadeDigitada(String(proximaQuantidade));
    setErrosDaBoleta((errosAnteriores) => ({ ...errosAnteriores, erroDaQuantidade: undefined }));
  }

  function aoSubmeterBoleta(eventoDeEnvioDaBoleta: FormEvent<HTMLFormElement>) {
    eventoDeEnvioDaBoleta.preventDefault();
    setErrosDaBoleta({
      erroDaQuantidade: validacaoDaQuantidade.mensagemDeErro,
      erroDoPreco: validacaoDoPreco.mensagemDeErro,
    });
    if (quantidadeAceita === undefined || precoAceitoEmCentavos === undefined || enviandoOrdem) return;
    aoEnviarOrdem({ simbolo: simboloDaOrdem, lado: ladoDaOrdem, quantidade: quantidadeAceita, precoEmCentavos: precoAceitoEmCentavos });
  }

  const classeCssDoLado = ladoDaOrdem === 'Compra' ? 'compra' : 'venda';
  const { erroDaQuantidade, erroDoPreco } = errosDaBoleta;

  return (
    <form className="cartao boleta" onSubmit={aoSubmeterBoleta} noValidate aria-label="Boleta de ordem">
      <h2 className="cartao-titulo">Nova ordem</h2>

      <fieldset className="alternador">
        <legend className="sr">Lado da ordem</legend>
        <button type="button" className="alternador-opcao compra" aria-pressed={ladoDaOrdem === 'Compra'} onClick={() => setLadoDaOrdem('Compra')}>
          Compra
        </button>
        <button type="button" className="alternador-opcao venda" aria-pressed={ladoDaOrdem === 'Venda'} onClick={() => setLadoDaOrdem('Venda')}>
          Venda
        </button>
      </fieldset>

      <div className="campo">
        <label className="campo-rotulo" htmlFor="simbolo">Símbolo</label>
        <select
          id="simbolo"
          className="seletor"
          value={simboloDaOrdem}
          onChange={(eventoDoSeletor) => setSimboloDaOrdem(eventoDoSeletor.target.value as SimboloDaBoleta)}
        >
          {SIMBOLOS_DA_BOLETA.map((simboloDisponivel) => (
            <option key={simboloDisponivel} value={simboloDisponivel}>{simboloDisponivel}</option>
          ))}
        </select>
      </div>

      <div className="campo">
        <label className="campo-rotulo" htmlFor="quantidade">Quantidade de {simboloDaOrdem}</label>
        <div className={erroDaQuantidade ? 'quantidade invalida' : 'quantidade'}>
          <button type="button" onClick={() => ajustarQuantidadeEm(-1)} aria-label="Diminuir quantidade">−</button>
          <input
            id="quantidade"
            className="num"
            inputMode="numeric"
            autoComplete="off"
            value={quantidadeDigitada}
            aria-invalid={erroDaQuantidade ? true : undefined}
            aria-describedby={erroDaQuantidade ? 'erro-quantidade' : undefined}
            onChange={(eventoDaQuantidade) => {
              setQuantidadeDigitada(eventoDaQuantidade.target.value);
              setErrosDaBoleta((errosAnteriores) => ({ ...errosAnteriores, erroDaQuantidade: undefined }));
            }}
          />
          <button type="button" onClick={() => ajustarQuantidadeEm(1)} aria-label="Aumentar quantidade">+</button>
        </div>
        {erroDaQuantidade && <p id="erro-quantidade" className="erro-campo" role="alert">{erroDaQuantidade}</p>}
      </div>

      <div className="campo">
        <label className="campo-rotulo" htmlFor="preco">Preço por ação (R$)</label>
        <input
          id="preco"
          className="entrada num"
          inputMode="decimal"
          autoComplete="off"
          placeholder="0,00"
          value={precoDigitado}
          aria-invalid={erroDoPreco ? true : undefined}
          aria-describedby={erroDoPreco ? 'erro-preco' : undefined}
          onChange={(eventoDoPreco) => {
            setPrecoDigitado(eventoDoPreco.target.value);
            setErrosDaBoleta((errosAnteriores) => ({ ...errosAnteriores, erroDoPreco: undefined }));
          }}
        />
        {erroDoPreco && <p id="erro-preco" className="erro-campo" role="alert">{erroDoPreco}</p>}
      </div>

      <dl className="resumo">
        <div className="resumo-linha">
          <dt>Preço por ação</dt>
          <dd className="num">{precoAceitoEmCentavos !== undefined ? formatarReais(precoAceitoEmCentavos / 100) : '—'}</dd>
        </div>
        <div className="resumo-linha">
          <dt>Valor total estimado</dt>
          <dd className="num" data-testid="total-estimado">
            {totalEstimadoEmReais !== undefined ? formatarReais(totalEstimadoEmReais) : '—'}
          </dd>
        </div>
      </dl>

      <button type="submit" className={`botao-enviar ${classeCssDoLado}`} disabled={enviandoOrdem}>
        {enviandoOrdem ? 'Enviando…' : ladoDaOrdem === 'Compra' ? 'Enviar ordem de compra' : 'Enviar ordem de venda'}
      </button>
      <p className="nota">Ordem de demonstração: nenhuma operação real é feita.</p>
    </form>
  );
}
