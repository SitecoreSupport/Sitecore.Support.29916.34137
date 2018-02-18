using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sitecore.Framework.Publishing.DataPromotion;
using Sitecore.Framework.Publishing.Item;
using Sitecore.Framework.Publishing.Locators;
using Sitecore.Framework.Publishing.Manifest;
using Sitecore.Framework.Publishing;

namespace Sitecore.Support.Framework.Publishing.DataPromotion
{
  public class DefaultItemCloneManifestPromoter : Sitecore.Framework.Publishing.DataPromotion.DefaultItemCloneManifestPromoter
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

    public override async Task Promote(
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
                          relationshipRepository.Save(targetContext.TargetStore.ScDatabaseName, declonedData.ToDictionary(d => (IItemVariantIdentifier)d.Item1, d => (IReadOnlyCollection<IItemRelationship>)d.Item2))); // Sitecore.Support.34137
                    },
                  _options.BatchSize,
                  cancelTokenSource);
      },
      cancelTokenSource);
    }

    /// <summary>
    /// processes the locators to build the decloned variants
    /// </summary>
    protected override async Task<IEnumerable<Tuple<IItemVariant, IItemRelationship[]>>> DecloneVariants(
        TargetPromoteContext targetContext,
        IItemReadRepository itemRepository,
        IItemRelationshipRepository relationshipRepository,
        IItemVariantLocator[] cloneLocators)
    {
      // get the clones..
      var cloneVariantsTask = itemRepository.GetVariants(cloneLocators);
      var cloneRelationshipsTask = relationshipRepository.GetOutRelationships(targetContext.SourceStore.Name, cloneLocators);// Sitecore.Support.34137
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
      var cloneSourceRelationshipsTask = relationshipRepository.GetOutRelationships(targetContext.SourceStore.Name, cloneSourceLocators); // Sitecore.Support.34137
      await Task.WhenAll(cloneSourceVariantsTask, cloneSourceRelationshipsTask).ConfigureAwait(false);

      var cloneSourceVariants = cloneSourceVariantsTask.Result.ToDictionary(x => (IItemVariantIdentifier)x, x => x, VariantIdentifierComparer);
      var cloneSourceRelationships = cloneSourceRelationshipsTask.Result;

      return cloneVariants
          .Select(cloneVariantEntry =>
          {
            List<IItemVariant> sourceVariants = null;

            IReadOnlyCollection<IItemRelationship> cloneRels;
            if (!cloneRelationships.TryGetValue(cloneVariantEntry.clone, out cloneRels))
            {
              cloneRels = new IItemRelationship[0];
            }

            if (cloneVariantEntry.cloneSourceIdentifier != null && // this should never be null though!
                      GetCloneSourceVariantData(cloneSourceVariants, cloneVariantEntry.cloneSourceIdentifier, out sourceVariants)) // Sitecore.Support.29916
                  {
                    #region Sitecore.Support.29916
                    var sourcesRels = new List<IReadOnlyCollection<IItemRelationship>>();

              IReadOnlyCollection<IItemRelationship> sourceRels;

              var store = cloneVariantEntry.cloneSourceIdentifier.Store;

              foreach (var sourceVariant in sourceVariants)
              {
                IItemVariantLocator localCloneSourceIdentifier;

                if (ItemVariantCloneExtensions.TryGetCloneSourceVariantUri(sourceVariant, store,
                          out localCloneSourceIdentifier))
                {
                  if (cloneSourceRelationships.TryGetValue(localCloneSourceIdentifier,
                            out sourceRels))
                  {
                    sourcesRels.Add(sourceRels);
                  }
                }
              }

                    // decloneCloneFields
                    return MergeCloneAndSourceVariants(
                        cloneVariantEntry.clone,
                        cloneRels,
                        sourceVariants,
                        sourcesRels);
                    #endregion
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

    #region Sitecore.Support.29916
    private bool GetCloneSourceVariantData(Dictionary<IItemVariantIdentifier, IItemVariant> cloneSourceVariants,
        IItemVariantLocator cloneSourceIdentifier, out List<IItemVariant> sourceVariants)
    {
      sourceVariants = new List<IItemVariant>();

      IItemVariant sourceItem;

      var localCloneSourceIdentifier = cloneSourceIdentifier;

      var store = cloneSourceIdentifier.Store;

      while (localCloneSourceIdentifier != null
          && cloneSourceVariants.TryGetValue(localCloneSourceIdentifier, out sourceItem))
      {
        if (sourceItem != null)
        {
          var isSourceItemInSourceList = sourceVariants.Any(s =>
              s.Id == sourceItem.Id
              && s.Language == sourceItem.Language
              && s.Version == sourceItem.Version);

          if (isSourceItemInSourceList)
          {
            // If source item is already in the list we should stop populating it to avoid circular dependencies.
            break;
          }

          sourceVariants.Add(sourceItem);

          IItemVariantLocator innerCloneSourceIdentifier;

          if (ItemVariantCloneExtensions.TryGetCloneSourceVariantUri(sourceItem, store,
              out innerCloneSourceIdentifier))
          {
            localCloneSourceIdentifier = innerCloneSourceIdentifier;
          }
          else
          {
            localCloneSourceIdentifier = null;
          }
        }
      }
      var hasCloneAnySource = sourceVariants.Count > 0;

      return hasCloneAnySource;
    }
    #endregion

    protected virtual Tuple<IItemVariant, IItemRelationship[]> MergeCloneAndSourceVariants(
        IItemVariant cloneVariant,
        IEnumerable<IItemRelationship> cloneRelationships,// Sitecore.Support.29916
        IList<IItemVariant> sourceVariants,
        IList<IReadOnlyCollection<IItemRelationship>> sourcesRelationships) // Sitecore.Support.29916
    {
      // item data
      var itemData = cloneVariant.Properties;

      #region Sitecore.Support.29916
      var fields = cloneVariant.Fields;

      IReadOnlyList<IFieldData> fieldsData = null;

      foreach (var sourceVariant in sourceVariants)
      {
        // fields data
        fieldsData = fields
            // remove the clone (source) fields
            .Where(x => x.FieldId != PublishingConstants.Clones.SourceItem &&
                        x.FieldId != PublishingConstants.Clones.SourceVariant)
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
        fields = fieldsData;
      }
      #endregion

      // links data
      var linksFromClone = cloneRelationships
          .Where(cloneRel =>
                  // Sitecore.Support.34137 +++
                  // the 'Source' and '_Source Item' fields should be skipped
                  cloneRel.Type != ItemRelationshipType.CloneOf &&
                  cloneRel.Type != ItemRelationshipType.CloneVersionOf &&
                  // Sitecore.Support.34137 ---

                  // this is a special relationship that captures the link between an item and its template
                  // this needs to be processed once .. hence it's being taken from the source and skiped from the clone (both must have the same template id)
                  (cloneRel.Type != ItemRelationshipType.TemplatedBy &&
                         !cloneVariant.Fields.Any() ||
                         cloneRel.SourceFieldId == null ||
                         cloneVariant.Fields.Any(c => c.FieldId == cloneRel.SourceFieldId)))
          .ToArray();

      #region Sitecore.Support.29916
      var linksFromSources = sourcesRelationships
      .Select(source => source
          .Select(x => new ItemRelationship(
              Guid.NewGuid(), // generate a new id to avoid database unique key constraint as all items from all targets are saved in the same links table!
              sourceVariants.Any(s => s.Id == x.SourceId)
                  ? cloneVariant.Id
                  : x.SourceId, // replace the sourceId with the cloned item id
              x.TargetId,
              x.Type,
              x.SourceVariance,
              x.TargetVariance, x.TargetPath, x.SourceFieldId)));

      IItemRelationship[] linksData = new IItemRelationship[0];

      // use the links from clone + any additional links from the source
      foreach (var linksFromSource in linksFromSources)
      {
        linksData = linksFromClone
            .Concat(linksFromSource.Where(x =>
                !linksFromClone.Any(s => s.SourceId == x.SourceId &&
                                         s.SourceFieldId == x.SourceFieldId &&
                                         x.Type == s.Type)))
            .ToArray();

        linksFromClone = linksData;
      }
      #endregion

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
  }
}