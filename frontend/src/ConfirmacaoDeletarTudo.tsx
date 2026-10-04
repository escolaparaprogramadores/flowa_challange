import { useRef, useState, type MouseEvent, type SyntheticEvent } from 'react';
import { IconeAlerta, IconeLixeira } from './Icones';

type PropsDaConfirmacaoDeletarTudo = {
  // Apaga no servidor e relê a tela; lança Error com a mensagem para a pessoa quando o apagar falha.
  aoConfirmarDeletarTudo: () => Promise<void>;
};

export function ConfirmacaoDeletarTudo({ aoConfirmarDeletarTudo }: PropsDaConfirmacaoDeletarTudo) {
  const janelaDeConfirmacao = useRef<HTMLDialogElement>(null);
  const [apagandoTudo, setApagandoTudo] = useState(false);
  const [mensagemDeErroDoApagar, setMensagemDeErroDoApagar] = useState<string>();

  function aoClicarEmDeletarTudoNoCabecalho() {
    setMensagemDeErroDoApagar(undefined);
    janelaDeConfirmacao.current?.showModal();
  }

  function fecharJanelaDeConfirmacao() {
    janelaDeConfirmacao.current?.close();
  }

  // Enquanto o apagar está no servidor a janela fica aberta: é nela que aparece o erro.
  function aoApertarEscNaJanela(eventoDeCancelamentoDaJanela: SyntheticEvent<HTMLDialogElement>) {
    if (apagandoTudo) eventoDeCancelamentoDaJanela.preventDefault();
  }

  // O clique no fundo escurecido chega com o próprio <dialog> como alvo; o clique no conteúdo, não.
  function aoClicarNoFundoDaJanela(eventoDeCliqueNaJanela: MouseEvent<HTMLDialogElement>) {
    if (eventoDeCliqueNaJanela.target === eventoDeCliqueNaJanela.currentTarget && !apagandoTudo) fecharJanelaDeConfirmacao();
  }

  async function aoConfirmarDeletarTudoNaJanela() {
    setApagandoTudo(true);
    setMensagemDeErroDoApagar(undefined);
    try {
      await aoConfirmarDeletarTudo();
      fecharJanelaDeConfirmacao();
    } catch (falhaNoApagar) {
      setMensagemDeErroDoApagar((falhaNoApagar as Error).message);
    } finally {
      setApagandoTudo(false);
    }
  }

  return (
    <>
      <button type="button" className="botao-deletar-tudo" onClick={aoClicarEmDeletarTudoNoCabecalho}>
        <IconeLixeira />
        Deletar tudo
      </button>
      <dialog
        ref={janelaDeConfirmacao}
        className="janela-confirmacao"
        aria-labelledby="titulo-confirmacao-deletar"
        aria-describedby="texto-confirmacao-deletar"
        onCancel={aoApertarEscNaJanela}
        onClick={aoClicarNoFundoDaJanela}
      >
        <div className="janela-confirmacao-conteudo">
          <span className="janela-confirmacao-icone">
            <IconeAlerta />
          </span>
          <h2 className="janela-confirmacao-titulo" id="titulo-confirmacao-deletar">Deletar todos os dados?</h2>
          <p className="janela-confirmacao-texto" id="texto-confirmacao-deletar">
            Todas as ordens serão apagadas e a exposição de PETR4, VALE3 e VIIA4 volta para R$ 0,00. Isso não pode ser desfeito.
          </p>
          {mensagemDeErroDoApagar && (
            <p className="janela-confirmacao-erro" role="alert">{mensagemDeErroDoApagar}</p>
          )}
          <div className="janela-confirmacao-acoes">
            <button type="button" className="janela-confirmacao-cancelar" onClick={fecharJanelaDeConfirmacao} disabled={apagandoTudo}>
              Cancelar
            </button>
            <button type="button" className="janela-confirmacao-deletar" onClick={aoConfirmarDeletarTudoNaJanela} disabled={apagandoTudo}>
              {apagandoTudo ? 'Apagando…' : 'Deletar tudo'}
            </button>
          </div>
        </div>
      </dialog>
    </>
  );
}
