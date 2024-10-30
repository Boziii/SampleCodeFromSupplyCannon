using Microsoft.Extensions.Configuration;
using RAWsupply.ProvisionLocal.Domain.Helpers;
using RAWsupply.ProvisionLocal.Domain.Models;
using RAWsupply.ProvisionLocal.Domain.Enums;
using RAWsupply.ProvisionLocal.Service.Database;
using System.Collections.Generic;
using System.Security.Principal;
using RAWsupply.Mediator;
using RAWsupply.ProvisionLocal.Domain.SupplierSync.Command.InsertSyncResponse;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Xml;
using System.Globalization;
using RAWsupply.ProvisionLocal.Domain.SupplierSync.Models;
using System;
using RAWsupply.ProvisionLocal.Domain.SupplierSync.Query.SyncStillRunning;
using RAWsupply.ProvisionLocal.Domain.CosmosDb;
using RAWsupply.ProvisionLocal.Domain.Products.Models;
using Newtonsoft.Json;

namespace RAWsupply.ProvisionLocal.Domain.Supplier.NewSysco.ParseProducts
{
    public class ParseProductsHandler : BaseQueryHandler<ParseProductsQuery, List<ParsedProduct>>
    {
        private Serilog.ILogger logger { get; set; }

        public ParseProductsHandler(IRAWsupplyDbContext repository, IConfiguration configuration, IIdentity identity, IMediator mediator, ICosmosDbService cosmosDbService, IHttpClientHelper httpClientHelper) : base(repository, configuration, identity, mediator, cosmosDbService, httpClientHelper)
        { }


        public async override Task<List<ParsedProduct>> HandleAsync(ParseProductsQuery query)
        {
            var contextName = SerilogHelper.GetEnrichedContextValue("NewSysco", Enums.ProcessType.ParsingProducts, query.CustomerId.ToString());
            logger = Serilog.Log.ForContext(SerilogHelper.CustomProperty, contextName);
            logger.Information("NewSysco Parsing Products");

            var products = new List<ParsedProduct>();
            var responseToParse = query.SupplierResponseData;

            try
            {
                // todo - parse products
                if (query.FavoritesOnly)
                {
                    products = parseFavoriteProducts(responseToParse, query);
                }
                else
                {
                    products = parseFullProducts(responseToParse, query);
                }
            }
            catch (Exception ex)
            {
                logger.Error("NewSysco Server Side Parsing Failed: {0}, {1}, {2}", ex.Message, ex.InnerException, ex.StackTrace);
            }


            logger.Information("NewSysco Parsing Products Finished");

            return products;
        }

        public List<ParsedProduct> parseFavoriteProducts(string body, ParseProductsQuery query)
        {
            var products = new List<ParsedProduct>();

            dynamic productJson = JsonConvert.DeserializeObject(body);
            var rows = productJson.productList;
            var priceList = productJson.prices;
            int count = 0;
            if (rows == null) return products;
            foreach (var row in rows)
            {
                var itemNumber = row.id;
                var description = row.description;
                var brand = row.detail.brand;

                var Pack = row.detail.packSize.pack;
                string SizeText = row.detail.packSize.size;
                SizeText = SizeText.Replace("0Z", "OZ");
                var productWeight = row.averageWeightPerCase;


                List<string> bodyArray = DecipherForPackSize(SizeText);
                var Size = bodyArray[0].Trim();
                var UOM = bodyArray[1].Trim();

                var price = "0";
                if (priceList[count] != null && priceList[count].supc == itemNumber)
                    price = priceList[count].price;
                var priceInPounds = row.isCatchWeight;

                var stockTypeFlag = row.detail.stockType; //not "" if remote stock
                var phaseOutFlag = row.detail.isPhasedOut; //is true if phased out
                
                if ((stockTypeFlag != "") || (phaseOutFlag == true))
                {
                    price = "0";
                }

                products.Add(new ParsedProduct()
                {
                    SupplierId = query.SupplierId,
                    ItemNumber = itemNumber,
                    Title = description,
                    Description = description,
                    Brand = brand,
                    MinRequiredUnits = "",
                    MaxAllowedUnits = "",
                    UnitsRemaining = "",
                    Price = price,
                    PriceInPounds = priceInPounds,
                    PackSize = Pack + "@" + Size,
                    ProductWeight = productWeight,
                    SizeUOM = UOM,
                    UnitOfMeasure = UOM,
                    Category = query.SupplierCategory,
                    Favorite = query.FavoritesOnly
                });

                count++;

            }

            return products;
        }

        public List<ParsedProduct> parseFullProducts(string body, ParseProductsQuery query)
        {
            var products = new List<ParsedProduct>();

            dynamic productJson = JsonConvert.DeserializeObject(body);
            var rows = productJson.productList.results;
            var priceList = productJson.prices;
            int count = 0;
            if (rows == null) return products;
            foreach (var row in rows)
            {
                var itemNumber = row.materialId;
                var description = row.description;
                var brand = row.brand;

                var Pack = row.packSize.pack;
                string SizeText = row.packSize.size;
                SizeText = SizeText.Replace("0Z", "OZ");
                var productWeight = row.averageWeightPerCase;


                List<string> bodyArray = DecipherForPackSize(SizeText);
                var Size = bodyArray[0].Trim();
                var UOM = bodyArray[1].Trim();
                var sizeUOM = UOM;

                var unitOfMeasure = row.packSize.unitOfMeasure;

                var price = "0";
                if (priceList[count] != null && priceList[count].supc == itemNumber)
                    price = priceList[count].price;
                var priceInPounds = row.isCatchWeight;

                var stockTypeFlag = row.stockType; //not "" if remote stock
                var phaseOutFlag = row.isPhasedOut; //is true if phased out
                
                if ((stockTypeFlag != "") || (phaseOutFlag == true))
                {
                    price = "0";
                }

                products.Add(new ParsedProduct()
                {
                    SupplierId = query.SupplierId,
                    ItemNumber = itemNumber,
                    Title = description,
                    Description = description,
                    Brand = brand,
                    MinRequiredUnits = "",
                    MaxAllowedUnits = "",
                    UnitsRemaining = "",
                    Price = price,
                    PriceInPounds = priceInPounds,
                    PackSize = Pack + "@" + Size,
                    ProductWeight = productWeight,
                    SizeUOM = sizeUOM,
                    UnitOfMeasure = unitOfMeasure,
                    Category = query.SupplierCategory,
                    Favorite = query.FavoritesOnly
                });

                count++;

            }

            return products;
        }

        public List<string> DecipherForPackSize(string body)
        {
            string SizeParsePattern = "(#?[0-9\\.]([ #xX\\.\\/-]*[0-9]+)*)([ \"#a-zA-Z]*.*)";

            
            Match bodyArray = Regex.Match(body, SizeParsePattern);
            var Size = bodyArray.Success ? bodyArray.Groups[1].ToString().Trim() : "1";
            var UOM = bodyArray.Success ? bodyArray.Groups[3].ToString().Trim() : body;


            List<string> DPU = new List<string>() {
                Size,
                UOM
            };

            return DPU;
        }
    }
}
