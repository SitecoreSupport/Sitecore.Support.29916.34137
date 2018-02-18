using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sitecore.Framework.Conditions;
using Sitecore.Framework.Publishing.Item;
using Sitecore.Framework.Publishing.Locators;
using Sitecore.Framework.Publishing.Manifest;

namespace Sitecore.Framework.Publishing.DataPromotion
{
    public class DefaultItemCloneManifestPromoter : ManifestPromoterBase, IItemCloneManifestPromoter
    {
        private static readonly IItemVariantIdentifierComparer VariantIdentifierComparer = new IItemVariantIdentifierComparer();

        private readonly PromoterOptions _options;

        public DefaultItemCloneManifestPromoter(
            ILogger<DefaultItemCloneManifestPromoter> logger,
            PromoterOptions options = null) : base(logger)
        {
            _options = options ?? new PromoterOptions();
        }

        public DefaultItemCloneManifestPromoter(
            ILogger<DefaultItemCloneManifestPromoter> logger,
            IConfiguration config) : this(
                logger,
                config.As<PromoterOptions>())
        {
        }

        public virtual async Task Promote(
            TargetPromoteContext targetContext,
            IManifestRepository manifestRepository,
            IItemReadRepository sourceItemRepository,
            IItemRelationshipRepository relationshipRepository,
            IItemWriteRepository targetItemRepository,
            FieldReportSpecification fieldsToReport,
            CancellationTokenSource cancelTokenSource)
        {
            await base.Promote(async () =>
            {
                var itemWorker = CreatePromoteWorker(manifestRepository, targetItemRepository, targetContext.Manifest.ManifestId, targetContext.CalculateResults, fieldsToReport);

                await ProcessManifestInBatches(
                    manifestRepository,
                    targetContext.Manifest.ManifestId,
                    ManifestStepAction.PromoteCloneVariant,
                    async (ItemVariantLocator[] batchUris) =>
                    {
                        return await DecloneVariants(targetContext, sourceItemRepository, relationshipRepository, batchUris).ConfigureAwait(false);
                    },
                    async declonedData =>
                    {
                        await Task.WhenAll(
                            itemWorker.SaveVariants(declonedData.Select(d => d.Item1).ToArray()),
                            relationshipRepository.Save(targetContext.TargetStore.Name, declonedData.ToDictionary(d => (IItemVariantIdentifier)d.Item1, d => (IReadOnlyCollection<IItemRelationship>) d.Item2)));
                    },
                    _options.BatchSize,
                    cancelTokenSource);
            },
            cancelTokenSource);
        }

        /// <summary>
        /// processes the locators to build the decloned variants
        /// </summary>
        protected virtual async Task<IEnumerable<Tuple<IItemVariant, IItemRelationship[]>>> DecloneVariants(
            TargetPromoteContext targetContext,
            IItemReadRepository itemRepository,
            IItemRelationshipRepository relationshipRepository,
            IItemVariantLocator[] cloneLocators)
        {
            // get the clones..
            var cloneVariantsTask = itemRepository.GetVariants(cloneLocators);
            var cloneRelationshipsTask = relationshipRepository.GetOutRelationships(targetContext.TargetStore.Name, cloneLocators);
            await Task.WhenAll(cloneVariantsTask, cloneRelationshipsTask).ConfigureAwait(false);

            var cloneVariants = cloneVariantsTask.Result
                .Select(v =>
                    {
                        IItemVariantLocator cloneSourceUri;
                        v.TryGetCloneSourceVariantUri("not_important", out cloneSourceUri); // we don't care about the store - it will be the same
                        return new
                        {
                            clone = v,
                            cloneSourceIdentifier = cloneSourceUri
                        };
                    })
                .ToArray();

            var cloneRelationships = cloneRelationshipsTask.Result;

            // get the clone sources..
            var cloneSourceLocators = cloneVariants
                .Select(v => v.cloneSourceIdentifier)
                .Where(i => i != null)
                .Distinct(VariantIdentifierComparer)
                .ToArray();

            var cloneSourceVariantsTask = itemRepository.GetVariants(cloneSourceLocators);
            var cloneSourceRelationshipsTask = relationshipRepository.GetOutRelationships(targetContext.TargetStore.Name, cloneSourceLocators);
            await Task.WhenAll(cloneSourceVariantsTask, cloneSourceRelationshipsTask).ConfigureAwait(false);

            var cloneSourceVariants = cloneSourceVariantsTask.Result.ToDictionary(x => (IItemVariantIdentifier)x, x => x, VariantIdentifierComparer);
            var cloneSourceRelationships = cloneSourceRelationshipsTask.Result;

            return cloneVariants
                .Select(cloneVariantEntry =>
                {
                    IItemVariant sourceVariant = null;

                    IReadOnlyCollection<IItemRelationship> cloneRels;
                    if (!cloneRelationships.TryGetValue(cloneVariantEntry.clone, out cloneRels))
                    {
                        cloneRels = new IItemRelationship[0];
                    }

                    if (cloneVariantEntry.cloneSourceIdentifier != null && // this should never be null though!
                        cloneSourceVariants.TryGetValue(cloneVariantEntry.cloneSourceIdentifier, out sourceVariant))
                    {
                        IReadOnlyCollection<IItemRelationship> sourceRels;
                        if (!cloneSourceRelationships.TryGetValue(cloneVariantEntry.cloneSourceIdentifier,
                            out sourceRels))
                        {
                            sourceRels = new IItemRelationship[0];
                        }

                        // decloneCloneFields
                        return MergeCloneAndSourceVariants(
                            cloneVariantEntry.clone,
                            cloneRels,
                            sourceVariant,
                            sourceRels);
                    }
                    else
                    {
                        // Clone Source is missing from DB
                        _logger.LogWarning("Clone Source Item {ItemId} does not exist on source database. Clones will still be published.", cloneVariantEntry.clone.Id);

                        // create a clone ItemVariant that doesn't contain the clone fields
                        var cloneItemVariant = new ItemVariant(
                            cloneVariantEntry.clone.Id,
                            cloneVariantEntry.clone.Language,
                            cloneVariantEntry.clone.Version,
                            cloneVariantEntry.clone.Revision,
                            cloneVariantEntry.clone.Properties,
                            cloneVariantEntry.clone.Fields.Where(x =>
                                x.FieldId != PublishingConstants.Clones.SourceItem &&
                                x.FieldId != PublishingConstants.Clones.SourceVariant).ToArray()
                        );

                        return new Tuple<IItemVariant, IItemRelationship[]>(cloneItemVariant, cloneRels.ToArray());
                    }
                })
                .Where(x => x != null)
                .ToArray();
        }

        protected virtual VarianceInfo CreateFieldVarianceInfo(IItemVariant cloneVariant, IFieldData fieldData)
        {
            // Always make sure that we return the langugae of the clone item
            // This covers the scenario where the Source of the clone item is set to be in a different language TFS issue #29619
            switch (fieldData.Variance.VarianceType)
            {
                case VarianceType.Variant: return VarianceInfo.Variant(cloneVariant.Language, cloneVariant.Version);
                case VarianceType.LanguageVariant: return VarianceInfo.LanguageVariant(cloneVariant.Language);
                case VarianceType.Invariant: return VarianceInfo.Invariant;
            }
            return null;
        }

        protected virtual Tuple<IItemVariant, IItemRelationship[]> MergeCloneAndSourceVariants(
            IItemVariant cloneVariant,
            IEnumerable<IItemRelationship> cloneRelationships,
            IItemVariant sourceVariant,
            IEnumerable<IItemRelationship> sourceRelationships)
        {
            // item data
            var itemData = cloneVariant.Properties;

            // fields data
            var fieldsData = cloneVariant.Fields
                    // remove the clone (source) fields
                    .Where(x => x.FieldId != PublishingConstants.Clones.SourceItem && x.FieldId != PublishingConstants.Clones.SourceVariant)
                    .Concat(
                        // only use the fields from source if they don't exist in the clone
                        sourceVariant.Fields
                            .Where(x => cloneVariant.Fields.All(c => c.FieldId != x.FieldId))
                            .Select(x => new FieldData(
                                x.FieldId,
                                cloneVariant.Id,
                                x.RawValue,
                                CreateFieldVarianceInfo(cloneVariant, x))))
                     .ToArray();

            // links data
            var linksFromClone = cloneRelationships
                .Where(cloneRel =>
                        // this is a special relationship that captures the link between an item and its template
                        // this needs to be processed once .. hence it's being taken from the source and skiped from the clone (both must have the same template id)
                        cloneRel.Type != ItemRelationshipType.TemplatedBy &&
                               !cloneVariant.Fields.Any() ||
                               cloneRel.SourceFieldId == null ||
                               cloneVariant.Fields.Any(c => c.FieldId == cloneRel.SourceFieldId))
                .ToArray();

            var linksFromSource = sourceRelationships
                .Select(x => new ItemRelationship(
                    Guid.NewGuid(), // generate a new id to avoid database unique key constraint as all items from all targets are saved in the same links table!
                    x.SourceId == sourceVariant.Id ? cloneVariant.Id : x.SourceId, // replace the sourceId with the cloned item id
                    x.TargetId,
                    x.Type,
                    x.SourceVariance,
                    x.TargetVariance, x.TargetPath, x.SourceFieldId))
                .ToArray();

            // use the links from clone + any additional links from the source
            var linksData = linksFromClone
                .Concat(linksFromSource.Where(x =>
                    !linksFromClone.Any(s => s.SourceId == x.SourceId &&
                                        s.SourceFieldId == x.SourceFieldId &&
                                        x.Type == s.Type)))
                .ToArray();

            return new Tuple<IItemVariant, IItemRelationship[]>(
                new ItemVariant(
                    cloneVariant.Id,
                    cloneVariant.Language,
                    cloneVariant.Version, 
                    cloneVariant.Revision, 
                    itemData, 
                    fieldsData),
                linksData);
        }

        protected virtual IItemPromoteWorker CreatePromoteWorker(IManifestRepository manifestRepo, IItemWriteRepository targetWriteRepo, Guid manifestId, bool calculateResults, FieldReportSpecification fieldsToReport)
        {
            return new ItemManifestPromoteWorker(manifestRepo, new CloningResultsItemRepository(targetWriteRepo), manifestId, calculateResults, fieldsToReport);
        }

        protected class CloningResultsItemRepository : IItemWriteRepository
        {
            private readonly IItemWriteRepository _innerWriteRepository;

            public CloningResultsItemRepository(IItemWriteRepository innerWriteRepository)
            {
                Condition.Requires(innerWriteRepository, nameof(innerWriteRepository)).IsNotNull();

                _innerWriteRepository = innerWriteRepository;
            }

            public void Dispose() => _innerWriteRepository.Dispose();

            public async Task<IEnumerable<ItemChange>> SaveVariants(IReadOnlyCollection<IItemVariant> variants, bool calculateChanges = true, FieldReportSpecification fieldsToReport = null)
            {
                var originalResults = await _innerWriteRepository.SaveVariants(variants, calculateChanges, fieldsToReport).ConfigureAwait(false);

                // ensure that every clone that was saved, generates an 'updated' save result, even when it's revision hasn't changed (when
                // it is being promoted because it's clone source was changed, which wouldn't trigger the revision to change).
                return originalResults
                    .Select(r =>
                        {
                            if (r.ChangeType != DataChangeType.Unchanged) return r;
                            return new ItemExists(
                                    new ItemPropertiesUpdated(
                                        r.Id,
                                        r.Properties.Data.Name,
                                        r.Properties.Data.TemplateId,
                                        r.Properties.Data.ParentId,
                                        r.Properties.Data.MasterId,
                                        r.Properties.Data.Name,
                                        r.Properties.Data.TemplateId,
                                        r.Properties.Data.ParentId,
                                        r.Properties.Data.MasterId),
                                    r.InvariantFields.Concat(
                                        r.LanguageVariantFields.SelectMany(lf => lf.Value)).Concat(
                                            r.VariantFields.SelectMany(vf => vf.Value)));
                        })
                      .ToArray();
            }

            public Task<ItemChange> DeleteVariant(IItemVariantIdentifier uri, bool calculateChanges = true) =>
                _innerWriteRepository.DeleteVariant(uri, calculateChanges);

            public Task<IEnumerable<ItemChange>> DeleteVariants(IReadOnlyCollection<IItemVariantIdentifier> uris, bool calculateChanges = true) =>
                _innerWriteRepository.DeleteVariants(uris, calculateChanges);

            public Task<ItemChange> DeleteItem(Guid id, bool calculateChanges = true) =>
                _innerWriteRepository.DeleteItem(id, calculateChanges);

            public Task<IEnumerable<ItemChange>> DeleteItems(IReadOnlyCollection<Guid> ids, bool calculateChanges = true) =>
                _innerWriteRepository.DeleteItems(ids, calculateChanges);
        }
    }
}