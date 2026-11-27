import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { LocalizationPipe } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import {
  DocumentListItemDto,
  DocumentService,
  FieldDefinitionDto,
  FieldValidationRuleDraftService,
  FieldValidationRuleDto,
  FieldValidationRuleOutcome,
  FieldValidationRuleTestResultDto,
} from '@dignite/ng.vault-extract';
import {
  MAX_INPUTS_PER_RULE,
  MAX_RULE_DESCRIPTION_LENGTH,
  MAX_RULES_PER_FIELD,
  RuleForm,
  RulesFormArray,
  buildRuleForm,
  buildRuleInputForm,
  toRuleDtos,
} from './field-validation-rules.form';

// How many of the type's documents the dry run offers, newest first.
const TEST_DOCUMENT_CHOICES = 20;

/**
 * The validation rules section of the field editor: the rules themselves, drafting them with the model (from a
 * sentence, or from the checks the field's description states), and trying them on one of the type's documents.
 *
 * The parent owns the form array and saves it with the field; this component edits it in place. Drafting and the dry
 * run read the field as saved, so both wait until the field exists. The component lives inside the modal's @if, so
 * closing the modal destroys it and cancels any request still in flight.
 */
@Component({
  selector: 'lib-field-validation-rules',
  templateUrl: './field-validation-rules.component.html',
  imports: [ReactiveFormsModule, LocalizationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FieldValidationRulesComponent implements OnInit {
  private readonly draftService = inject(FieldValidationRuleDraftService);
  private readonly documentService = inject(DocumentService);
  private readonly toaster = inject(ToasterService);
  private readonly destroyRef = inject(DestroyRef);

  /** The field's rules, owned and saved by the parent form. */
  readonly rules = input.required<RulesFormArray>();
  /** The field as saved; null while it is being created. */
  readonly field = input<FieldDefinitionDto | null>(null);
  /** The type's fields; a rule reads any of them except its own. */
  readonly fields = input<readonly FieldDefinitionDto[]>([]);
  readonly documentTypeCode = input('');
  /** The description as currently typed, which drafting from the description must not silently ignore. */
  readonly description = input('');

  readonly maxRules = MAX_RULES_PER_FIELD;
  readonly maxInputs = MAX_INPUTS_PER_RULE;
  readonly maxDescription = MAX_RULE_DESCRIPTION_LENGTH;
  readonly outcomes = FieldValidationRuleOutcome;

  readonly otherFields = computed(() => this.fields().filter(field => field.id !== this.field()?.id));
  readonly isSaved = computed(() => !!this.field()?.id);
  readonly savedDescription = computed(() => (this.field()?.description ?? '').trim());
  readonly descriptionUnsaved = computed(() => this.savedDescription() !== this.description().trim());

  readonly requirement = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(MAX_RULE_DESCRIPTION_LENGTH)],
  });
  readonly testDocumentId = new FormControl('', { nonNullable: true });

  readonly isDrafting = signal(false);
  readonly isDraftingFromDescription = signal(false);
  readonly isTesting = signal(false);
  /** Rules the model wrote in this editing session, marked until the field is saved. */
  readonly drafted = signal<ReadonlySet<RuleForm>>(new Set());
  readonly documents = signal<readonly DocumentListItemDto[]>([]);
  readonly documentsLoaded = signal(false);
  /** The last dry run's outcomes, by rule position; cleared whenever a rule changes. */
  readonly results = signal<readonly FieldValidationRuleTestResultDto[] | null>(null);

  constructor() {
    // The parent resolves the type's code asynchronously, so the documents load once it is known.
    effect(() => {
      const code = this.documentTypeCode();
      if (code && this.isSaved()) {
        untracked(() => this.loadDocuments(code));
      }
    });
  }

  ngOnInit(): void {
    this.rules().valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.results.set(null));
  }

  atLimit(): boolean {
    return this.rules().length >= MAX_RULES_PER_FIELD;
  }

  isBusy(): boolean {
    return this.isDrafting() || this.isDraftingFromDescription() || this.isTesting();
  }

  canDraft(): boolean {
    return this.isSaved() && !this.atLimit() && !this.isBusy()
      && !!this.requirement.value.trim() && this.requirement.valid;
  }

  canDraftFromDescription(): boolean {
    return this.isSaved() && !this.atLimit() && !this.isBusy()
      && !!this.savedDescription() && !this.descriptionUnsaved();
  }

  canTest(): boolean {
    return this.isSaved() && !this.isBusy() && this.rules().length > 0 && this.rules().valid
      && !!this.testDocumentId.value;
  }

  addRule(): void {
    if (this.atLimit()) return;
    this.rules().push(buildRuleForm());
  }

  removeRule(index: number): void {
    this.rules().removeAt(index);
  }

  addInput(rule: RuleForm): void {
    if (rule.controls.inputs.length >= MAX_INPUTS_PER_RULE) return;
    rule.controls.inputs.push(buildRuleInputForm());
  }

  removeInput(rule: RuleForm, index: number): void {
    rule.controls.inputs.removeAt(index);
  }

  isDrafted(rule: RuleForm): boolean {
    return this.drafted().has(rule);
  }

  resultOf(index: number): FieldValidationRuleTestResultDto | null {
    return this.results()?.[index] ?? null;
  }

  fieldLabel(field: FieldDefinitionDto): string {
    return field.displayName ? `${field.displayName} (${field.name})` : (field.name ?? '');
  }

  documentLabel(document: DocumentListItemDto): string {
    const name = document.title || document.fileOrigin?.originalFileName || document.id || '';
    const date = document.creationTime ? document.creationTime.slice(0, 10) : '';
    return date ? `${name} · ${date}` : name;
  }

  draft(): void {
    const field = this.field();
    const description = this.requirement.value.trim();
    if (!field?.id || !this.canDraft()) return;
    this.isDrafting.set(true);
    // `undefined` for the generated cancellationToken parameter: ASP.NET Core supplies RequestAborted.
    this.draftService.draft({ fieldId: field.id, description }, undefined)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.isDrafting.set(false);
          if (this.append(result.items ?? [])) {
            this.requirement.setValue('');
          }
        },
        error: () => this.isDrafting.set(false),
      });
  }

  draftFromDescription(): void {
    const field = this.field();
    if (!field?.id || !this.canDraftFromDescription()) return;
    this.isDraftingFromDescription.set(true);
    this.draftService.draftFromPrompt({ fieldId: field.id }, undefined)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.isDraftingFromDescription.set(false);
          this.append(result.items ?? []);
        },
        error: () => this.isDraftingFromDescription.set(false),
      });
  }

  test(): void {
    const field = this.field();
    if (!field?.id || !this.canTest()) return;
    this.isTesting.set(true);
    const rules = toRuleDtos(this.rules());
    const tried = JSON.stringify(rules);
    this.draftService.test({ fieldId: field.id, documentId: this.testDocumentId.value, rules }, undefined)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.isTesting.set(false);
          // The outcomes are by position: shown against rules edited while the run was out, they would sit beside the
          // wrong rule.
          if (JSON.stringify(toRuleDtos(this.rules())) === tried) {
            this.results.set(result.items ?? []);
          }
        },
        // The server's reason (a document of another type, an invalid rule) reaches the operator through ABP's
        // error handler.
        error: () => this.isTesting.set(false),
      });
  }

  /** Appends drafted rules up to the field's cap; false when the model drafted none. */
  private append(drafts: readonly FieldValidationRuleDto[]): boolean {
    if (drafts.length === 0) {
      this.toaster.info('VaultExtract::FieldDefinition:ValidationRules:DraftNone', 'VaultExtract::Info');
      return false;
    }
    const room = MAX_RULES_PER_FIELD - this.rules().length;
    const added = drafts.slice(0, Math.max(room, 0)).map(draft => buildRuleForm(draft));
    for (const rule of added) {
      this.rules().push(rule);
    }
    this.drafted.update(set => new Set([...set, ...added]));
    if (added.length < drafts.length) {
      this.toaster.warn('VaultExtract::FieldDefinition:ValidationRules:DraftCapped', 'VaultExtract::Warning', {
        messageLocalizationParams: [String(MAX_RULES_PER_FIELD)],
      });
    }
    return true;
  }

  private loadDocuments(documentTypeCode: string): void {
    this.documentService.getList({
      documentTypeCode,
      sorting: 'creationTime desc',
      skipCount: 0,
      maxResultCount: TEST_DOCUMENT_CHOICES,
    }, { skipHandleError: true })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: page => {
          this.documents.set(page.items ?? []);
          this.documentsLoaded.set(true);
        },
        // Reading documents is a separate permission from editing fields. Without it there is nothing to try a rule
        // on, which the empty choice says; an error dialog on opening the editor would only be noise.
        error: () => this.documentsLoaded.set(true),
      });
  }
}
