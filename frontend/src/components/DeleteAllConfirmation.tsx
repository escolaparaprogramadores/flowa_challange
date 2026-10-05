import { useEffect, useRef, useState, type MouseEvent, type SyntheticEvent } from 'react';
import { AlertIcon, TrashIcon } from './Icons';

type DeleteAllConfirmationProps = {
  // Deletes on the server and reads the screen again; throws an Error with the message for the person when deleting fails.
  onConfirmDeleteAll: () => Promise<void>;
};

export function DeleteAllConfirmation({ onConfirmDeleteAll }: DeleteAllConfirmationProps) {
  const confirmationDialog = useRef<HTMLDialogElement>(null);
  const [isDeletingAll, setIsDeletingAll] = useState(false);
  const [deleteErrorMessage, setDeleteErrorMessage] = useState<string>();

  function openDeleteAllConfirmation() {
    setDeleteErrorMessage(undefined);
    confirmationDialog.current?.showModal();
  }

  function closeConfirmationDialog() {
    confirmationDialog.current?.close();
  }

  // While the delete is on the server the dialog stays open: that is where the error shows up.
  function blockEscapeWhileDeleting(dialogCancelEvent: SyntheticEvent<HTMLDialogElement>) {
    if (isDeletingAll) dialogCancelEvent.preventDefault();
  }

  // Chrome only lets the dialog "cancel" be prevented once without a new click: the second Esc would close it.
  // With the buttons disabled the focus leaves the dialog, so the key is blocked on the whole document.
  useEffect(() => {
    if (!isDeletingAll) return;
    function blockEscapeKeyWhileDeleting(keyboardEvent: KeyboardEvent) {
      if (keyboardEvent.key === 'Escape') keyboardEvent.preventDefault();
    }
    document.addEventListener('keydown', blockEscapeKeyWhileDeleting, true);
    return () => document.removeEventListener('keydown', blockEscapeKeyWhileDeleting, true);
  }, [isDeletingAll]);

  // A click on the dimmed backdrop arrives with the <dialog> itself as target; a click on the content does not.
  function closeOnBackdropClick(dialogClickEvent: MouseEvent<HTMLDialogElement>) {
    if (dialogClickEvent.target === dialogClickEvent.currentTarget && !isDeletingAll) closeConfirmationDialog();
  }

  async function confirmDeleteAllInDialog() {
    setIsDeletingAll(true);
    setDeleteErrorMessage(undefined);
    try {
      await onConfirmDeleteAll();
      closeConfirmationDialog();
    } catch (deleteFailure) {
      setDeleteErrorMessage((deleteFailure as Error).message);
    } finally {
      setIsDeletingAll(false);
    }
  }

  return (
    <>
      <button type="button" className="delete-all-button" onClick={openDeleteAllConfirmation}>
        <TrashIcon />
        Deletar tudo
      </button>
      <dialog
        ref={confirmationDialog}
        className="confirmation-dialog"
        aria-labelledby="delete-confirmation-title"
        aria-describedby="delete-confirmation-text"
        onCancel={blockEscapeWhileDeleting}
        onClick={closeOnBackdropClick}
      >
        <div className="confirmation-dialog-content">
          <span className="confirmation-dialog-icon">
            <AlertIcon />
          </span>
          <h2 className="confirmation-dialog-title" id="delete-confirmation-title">Deletar todos os dados?</h2>
          <p className="confirmation-dialog-text" id="delete-confirmation-text">
            Todas as ordens serão apagadas e a exposição de PETR4, VALE3 e VIIA4 volta para R$ 0,00. Isso não pode ser desfeito.
          </p>
          {deleteErrorMessage && (
            <p className="confirmation-dialog-error" role="alert">{deleteErrorMessage}</p>
          )}
          <div className="confirmation-dialog-actions">
            <button type="button" className="confirmation-dialog-cancel" onClick={closeConfirmationDialog} disabled={isDeletingAll}>
              Cancelar
            </button>
            <button type="button" className="confirmation-dialog-delete" onClick={confirmDeleteAllInDialog} disabled={isDeletingAll}>
              {isDeletingAll ? 'Apagando…' : 'Deletar tudo'}
            </button>
          </div>
        </div>
      </dialog>
    </>
  );
}
