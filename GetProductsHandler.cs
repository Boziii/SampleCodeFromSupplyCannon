using Microsoft.Extensions.Configuration;
using PuppeteerSharp;
using RAWsupply.Mediator;
using RAWsupply.ProvisionLocal.Domain.Admin.Models;
using RAWsupply.ProvisionLocal.Domain.CosmosDb;
using RAWsupply.ProvisionLocal.Domain.Enums;
using RAWsupply.ProvisionLocal.Domain.Helpers;
using RAWsupply.ProvisionLocal.Domain.Models;
using RAWsupply.ProvisionLocal.Domain.Products.Command.SaveProductsFromSync;
using RAWsupply.ProvisionLocal.Domain.Supplier.NewSysco.ParseProducts;
using RAWsupply.ProvisionLocal.Domain.SupplierSync.Command.InsertSyncResponse;
using RAWsupply.ProvisionLocal.Domain.SupplierSync.Models;
using RAWsupply.ProvisionLocal.Domain.SupplierSync.Query.SyncStillRunning;
using RAWsupply.ProvisionLocal.Service.Database;
using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;

namespace RAWsupply.ProvisionLocal.Domain.Supplier.NewSysco.GetProducts
{

    public class GetProductsHandler : BaseQueryHandler<GetProductsQuery, bool>
    {
        private bool parseProductsOnClient { get; set; }
        private Serilog.ILogger logger { get; set; }
        private bool isScraperApiEnabled;
        private string scraperApiKey;

        private PuppeteerSupplierService SupplierService { get; set; }
        private GetProductsQuery Query { get; set; }
        private string DownloadPath { get; set; }
        private NavigationOptions NavigationOptions { get; set; }
        private WaitForSelectorOptions WaitForSelectorOptions { get; set; }
        private List<AdminSupplierCategory> CategoriesToPull { get; set; }
        private bool UsePuppeteer { get; set; }

        public GetProductsHandler(IRAWsupplyDbContext repository, IConfiguration configuration, IIdentity identity, IMediator mediator, ICosmosDbService cosmosDbService, IHttpClientHelper httpClientHelper) : base(repository, configuration, identity, mediator, cosmosDbService, httpClientHelper)
        {
            this.Configuration = configuration;
            this.scraperApiKey = this.Configuration["ScraperApi:ApiKey"];
            this.NavigationOptions = new NavigationOptions
            {
                Timeout = 60000
            };
            this.WaitForSelectorOptions = new WaitForSelectorOptions
            {
                Timeout = 60000
            };
        }

        private async Task SaveRawData(GetProductsQuery query, string rawData, string theCategory)
        {
            logger.Information("NewSysco Raw Data Save");
            if (this.parseProductsOnClient)
            {
                // save response to table for future parsing
                var response = await Mediator.HandleAsync(new InsertSyncResponseCommand
                {
                    RequestId = query.RequestId,
                    CustomerId = query.CustomerId,
                    ResponseHeader = new ResponseHeader
                    {
                        CustomerId = query.CustomerId,
                        SupplierId = Enums.Supplier.NewSysco,
                        FavoritesOnly = query.FavoritesOnly,
                        FullSyncOnly = !query.FavoritesOnly,
                        SubstituteItemsOnly = false,
                        ResponseType = SyncResponseType.JSON,
                        Category = theCategory

                    },
                    ResponseData = rawData
                });
            }
            else
            {
                // parse products
                var products = await Mediator.HandleAsync(new ParseProductsQuery
                {
                    CustomerId = query.CustomerId,
                    SupplierId = query.SupplierId,
                    FavoritesOnly = query.FavoritesOnly,
                    RequestId = query.RequestId,
                    SupplierCategory = theCategory,
                    SupplierResponseData = rawData
                });

                // save immediately
                var response = await Mediator.HandleAsync(new SaveProductsFromSyncCommand
                {
                    CustomerId = query.CustomerId,
                    SupplierId = query.SupplierId,
                    DeleteResponse = false,
                    FromInboundProductSync = false,
                    IsAdmin = true,
                    RequestId = query.RequestId,
                    ResponseId = 0,
                    RequestStatusId = 0,
                    ReturnProducts = false,
                    Products = products
                });
            }
        }

        private async Task SaveBlankData(GetProductsQuery query, string CategoryName)
        {
            if (this.parseProductsOnClient)
            {
                logger.Information("NewSysco Blank Data Save");

                // save response to table for future parsing
                var response = await Mediator.HandleAsync(new InsertSyncResponseCommand
                {
                    RequestId = query.RequestId,
                    CustomerId = query.CustomerId,
                    ResponseHeader = new ResponseHeader
                    {
                        CustomerId = query.CustomerId,
                        SupplierId = Enums.Supplier.NewSysco,
                        FavoritesOnly = query.FavoritesOnly,
                        FullSyncOnly = !query.FavoritesOnly,
                        SubstituteItemsOnly = false,
                        ResponseType = SyncResponseType.Blank,
                        Category = CategoryName

                    },
                    ResponseData = ""
                });
            }
        }

        private string cleanUpJson(string dirtyJson)
        {
            string cleanJson = "";
            var start = dirtyJson.IndexOf("{");
            var jsonBody = dirtyJson.Substring(start);

            var end = jsonBody.LastIndexOf("}");
            jsonBody = jsonBody.Substring(0, end + 1);

            string goodChunkPatterns = "\n(.*?\n)";
            MatchCollection goodChunks = Regex.Matches(jsonBody, goodChunkPatterns);
            jsonBody = Regex.Replace(jsonBody, goodChunkPatterns, "");

            while (jsonBody.IndexOf("\r") > 0)
            {
                jsonBody = jsonBody.Replace("\r", "");
            }

            cleanJson = jsonBody;

            return cleanJson;
        }

        private string cleanUpResponse(string dirtyResponse)
        {
            string cleanResponse = "";
            string responseBody = dirtyResponse;
            //
            string chunkPattern = "\n[0-9a-z]+(\n|\r)";
            MatchCollection chunkBreaks = Regex.Matches(responseBody, chunkPattern);
            responseBody = Regex.Replace(responseBody, chunkPattern, "");

            responseBody = Regex.Replace(responseBody, @"\r\n?|\n", "");

            cleanResponse = responseBody;

            return cleanResponse;
        }

        public async override Task<bool> HandleAsync(GetProductsQuery query)
        {
            var contextName = SerilogHelper.GetEnrichedContextValue("NewSysco", Enums.ProcessType.ProductsSync, query.CustomerId.ToString());
            logger = Serilog.Log.ForContext(SerilogHelper.CustomProperty, contextName);
            logger.Information("NewSysco Getting Products");

            var supplier = await Repository.Admin_Suppliers.FindAsync(query.SupplierId);
            this.parseProductsOnClient = supplier.ParseProductsOnClient.GetValueOrDefault();

            if (!this.parseProductsOnClient)
            {
                var sr = await Repository.Customer_SyncRequests.FindAsync(query.RequestId);
                sr.Querying = true;
                sr.Parsing = true;
                await Repository.SaveChangesAsync();
            }

            this.Query = query;
            this.DownloadPath = query.CSVDownloadPath;
            string username = query.Username;
            string password = query.Password;
            string chromiumExePath = query.ChromiumExePath;



            // get all products
            try
            {
                bool loggedIn = true;
                if (query.FavoritesOnly)
                {
                    loggedIn = await GetFavorites(query);
                }
                else
                {
                    loggedIn = await GetProducts(query);
                }
                if (loggedIn == false)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.Error("NewSysco HandleAsync Failed: {0}, {1}, {2}", ex.Message, ex.InnerException, ex.StackTrace);
            }

            this.WaitForAllTasksToComplete(); ;

            logger.Information("NewSysco Get Products Finished");

            return true;
        }

        private bool newSyscoSocketLogin(NewSyscoSockets supplierService, GetProductsQuery query)
        {
            var MSSResponse = supplierService.GetGuestResponse(); //POST /api/v1/auth/guest
            var SamlCSRFResponse = supplierService.GetCSRFTokenAndSAML(); //POST /api/v1/auth/sso
            Match csrfTokenPattern = Regex.Match(SamlCSRFResponse.Body, "redirectTo.:.(.*?).,.*csrfToken.:.([0-9a-zA-Z]*)");
            string csrfToken = csrfTokenPattern.Groups[2].ToString();

            var stateTokenResponse = supplierService.GetStateTokenResponse(csrfTokenPattern.Groups[1].ToString()); //GET /login/login.htm?AppName=SyscoShop&fromURI=
            Match stateTokenPattern = Regex.Match(stateTokenResponse.Body, "stateToken.:.(.*?).,");
            string stateToken = stateTokenPattern.Groups[1].ToString();
            stateToken = stateToken.Replace("\\x2D", "-");

            var introspectResponse = supplierService.GetIntrospectResponse(stateToken); //POST /api/v1/authn/introspect
            var loggedIn = supplierService.AlternateLogin(stateToken); //POST /api/v1/authn
            if (loggedIn == false)
            {
                return false;
            }
            var loginRedirectResponse = supplierService.loginRedirectResponse(stateToken); //GET /login/step-up/redirect?stateToken=
            var cleanedloginRedirectResponse = cleanUpResponse(loginRedirectResponse.Body);
            Match samlPattern = Regex.Match(cleanedloginRedirectResponse, "SAMLResponse.*?value=.(.*?)\"");
            Match relayStatePattern = Regex.Match(cleanedloginRedirectResponse, "RelayState.*?value=.(.*?)\"");
            string saml = samlPattern.Groups[1].ToString();
            string relayState = relayStatePattern.Groups[1].ToString();

            var secondRedirectResponse = supplierService.secondLoginRedirectResponse(saml, relayState); //POST /api/v1/auth/sso/assert
            var thirdRedirectResponse = supplierService.thirdLoginRedirectResponse(); //GET /app/discover?_auth_type=external
            var customersResponse = supplierService.GetCustomersResponse(csrfToken); //POST /api/v1/auth/validate?authType=AD
            Match opCoAndID = Regex.Match(customersResponse.Body, "opCo.:.([0-9a-zA-Z]*).*customerId.:.([0-9a-zA-Z]*)");
            string opCo = opCoAndID.Groups[1].ToString();
            string CustomerId = opCoAndID.Groups[2].ToString();

            string MSS_StatefulCookieName = "";
            string MSS_StatefulValue = "";
            foreach (var pair in supplierService.CookieMap)
            {
                if (pair.Key.StartsWith("MSS_STATEFUL"))
                {
                    MSS_StatefulCookieName = pair.Key;
                    MSS_StatefulValue = pair.Value;
                }
            }

            if (!string.IsNullOrEmpty(query.SupplierCustomerId))
            {
                string opcoIdPattern = "([0-9]+)-([0-9]+)";

                Match opcoId = Regex.Match(query.SupplierCustomerId, opcoIdPattern);
                opCo = opcoId.Groups[1].ToString();
                CustomerId = opcoId.Groups[2].ToString();
            }

            this.Query.AuthToken = new NewSyscoAuthToken
            {
                CRSFToken = csrfToken,
                CustomerId = CustomerId,
                MssStatefulCookieName = MSS_StatefulCookieName,
                MssStatefulValue = MSS_StatefulValue,
                OpCo = opCo
            };

            return true;
        }
        private async Task<bool> GetProducts(GetProductsQuery query)
        {
            string username = query.Username;
            string password = query.Password;

            this.isScraperApiEnabled = await IsScraperApiEnabled(Enums.Supplier.NewSysco);
            var supplierService = new NewSyscoSockets(username, password, this.isScraperApiEnabled, this.scraperApiKey);
            try
            {
                if (query.AuthToken == null)
                {
                    var loggedIn = newSyscoSocketLogin(supplierService, query);
                    if (loggedIn == false)
                    {
                        return false;
                    }
                    query.AuthToken = this.Query.AuthToken;
                }
                // SOCKET CALLS FOR GET PRODUCTS
                var CustomerInfo = await supplierService.SetCustomerAsync(query.AuthToken);

                int pageNum = 0;
                bool StillHasPages = true;
                string errorMessage = "message.:.*Internal Server Error";
                string noMoreProducts = @"results.:\[\]";
                string totalResultsPattern = "totalResults...*?([0-9]+)";
                for (int i = 1; i <= 12; i++)
                {
                    pageNum = 0;

                    var ProductCategory = await supplierService.getProducts2Async(i, pageNum, query.AuthToken);
                    while (ProductCategory.Headers.Count == 0)
                    {
                        ProductCategory = await supplierService.getProducts2Async(i, pageNum, query.AuthToken);
                    }
                    string cleanedJson = cleanUpJson(ProductCategory.Body);
                    Match totalResultsMatch = Regex.Match(cleanedJson, totalResultsPattern);
                    int totalResults = 0;
                    Int32.TryParse(totalResultsMatch.Groups[1].ToString(), out totalResults);
                    do
                    {
                        ProductCategory = await supplierService.getProducts2Async(i, pageNum, query.AuthToken);
                        while (ProductCategory.Headers.Count == 0)
                        {
                            ProductCategory = await supplierService.getProducts2Async(i, pageNum, query.AuthToken);
                        }
                        cleanedJson = cleanUpJson(ProductCategory.Body);
                        Match pageToFar = Regex.Match(cleanedJson, errorMessage);
                        Match noMorePages = Regex.Match(cleanedJson, noMoreProducts);
                        StillHasPages = !(pageToFar.Success) && !(noMorePages.Success);
                        if (StillHasPages)
                        {
                            var PricesResponse = await supplierService.getPricingAsync(cleanedJson, query.AuthToken, query.FavoritesOnly);
                            while (PricesResponse.Headers.Count == 0)
                            {
                                PricesResponse = await supplierService.getPricingAsync(cleanedJson, query.AuthToken, query.FavoritesOnly);
                            }
                            string cleanedPriceJson = cleanUpJson(PricesResponse.Body);

                            string productAndPriceJson = "{\"productList\": " + cleanedJson + ", \"prices\":[" + cleanedPriceJson + "]}";
                            var task = Task.Run(async () =>
                            {
                                await SaveRawData(query, productAndPriceJson, "SyscoCategory" + i + ", page " + pageNum);
                            });
                            await this.AddTaskAndRun(task);
                        }
                        pageNum++;
                    } while (StillHasPages);
                }
            }
            catch (Exception ex)
            {
                logger.Error("NewSysco GetProducts Failed: {0}, {1}, {2}", ex.Message, ex.InnerException, ex.StackTrace);
            }

            return true;
        }


        private async Task GetSubCategory(int i, string productJson, NewSyscoAuthToken authToken, NewSyscoSockets supplierService, GetProductsQuery query)
        {
            //totalResults...*?([0-9]+)
            //majCategory.:.*?({[^\[]*\[([^\]]*)])
            string majCategoryPattern = @"majCategory.:.*?({[^\[]*\[([^\]]*)])";
            string subCategoryIDPattern = "id.:.*?([0-9]+)";
            Match MajCategoryJson = Regex.Match(productJson, majCategoryPattern);
            MatchCollection SubCategoryIds = Regex.Matches(MajCategoryJson.Groups[2].ToString(), subCategoryIDPattern);
            int pageNum = 0;
            bool StillHasPages = true;
            string errorMessage = "message.:.*Internal Server Error";
            string noMoreProducts = "status.:.*no results";
            foreach (Match subIDMatch in SubCategoryIds)
            {
                pageNum = 0;
                string subID = subIDMatch.Groups[1].ToString();
                do
                {
                    var ProductCategory = await supplierService.getProductsSubCategoryAsync(i, pageNum, subID, authToken);
                    string cleanedJson = cleanUpJson(ProductCategory.Body);
                    Match pageToFar = Regex.Match(cleanedJson, errorMessage);
                    Match noMorePages = Regex.Match(cleanedJson, noMoreProducts);
                    StillHasPages = !(pageToFar.Success) && !(noMorePages.Success);
                    if (StillHasPages)
                    {

                        var PricesResponse = await supplierService.getPricingAsync(cleanedJson, authToken, query.FavoritesOnly);
                        string cleanedPriceJson = cleanUpJson(PricesResponse.Body);

                        string productAndPriceJson = "{\"productList\": " + cleanedJson + ", \"prices\":[" + cleanedPriceJson + "]}";
                        var task = Task.Run(async () =>
                        {
                            await SaveRawData(query, productAndPriceJson, "SyscoCategory" + i + ", page " + pageNum + ", subCategory " + subID);
                        });
                        await this.AddTaskAndRun(task);
                    }
                    pageNum++;
                } while (StillHasPages);
            }
        }


        private async Task<bool> GetFavorites(GetProductsQuery query)
        {
            string username = query.Username;
            string password = query.Password;
            List<string> dates = new List<string>();

            this.isScraperApiEnabled = await IsScraperApiEnabled(Enums.Supplier.NewSysco);
            var supplierService = new NewSyscoSockets(username, password, this.isScraperApiEnabled, this.scraperApiKey);
            try
            {
                logger.Information("NewSysco Raw Data Favorites");

                if (query.AuthToken == null)
                {
                    var loggedIn = newSyscoSocketLogin(supplierService, query);
                    if (loggedIn == false)
                    {
                        return false;
                    }
                }

                // SOCKET CALLS FOR GET PRODUCTS
                var CustomerInfo = await supplierService.SetCustomerAsync(query.AuthToken);

                var faveListIdsResponse = await supplierService.getFavListIdsAsync(query.AuthToken);
                string cleanedfaveListIdsResponse = "{\"results\":[" + cleanUpJson(faveListIdsResponse.Body) + "]}";

                dynamic productJson = JsonConvert.DeserializeObject(cleanedfaveListIdsResponse);
                var rows = productJson.results;
                string mapOnlyDataPattern = "map.*?{(.*)}.*?minData";
                string productsPattern = "[0-9]+.:({.*?isFavorite.*?})";
                string totalProductsPattern = "totalItems.*?([0-9]+)";

                foreach (var row in rows)
                {
                    string listId = row.id;
                    string listType = row.type;
                    bool isShared = row.is_shared != null ? row.is_shared : false;
                    if (isShared == false)
                    {
                        var favList = await supplierService.getFavoriteListAsync(query.AuthToken, listId, listType);
                        while (favList.Headers.Count == 0)
                        {
                            favList = await supplierService.getFavoriteListAsync(query.AuthToken, listId, listType);
                        }
                        string cleanedFavList = cleanUpJson(favList.Body);
                        Match totalItems = Regex.Match(cleanedFavList, totalProductsPattern);
                        if (totalItems.Groups[1].ToString() != "0")
                        {
                            Match MapOnlyData = Regex.Match(cleanedFavList, mapOnlyDataPattern);
                            string MapOnlyDataJson = MapOnlyData.Groups[1].ToString();
                            var priceJson = await supplierService.getPricingAsync(MapOnlyDataJson, query.AuthToken, query.FavoritesOnly);
                            while (priceJson.Headers.Count == 0)
                            {
                                priceJson = await supplierService.getPricingAsync(MapOnlyDataJson, query.AuthToken, query.FavoritesOnly);
                            }

                            string cleanedPriceJson = cleanUpJson(priceJson.Body);

                            MatchCollection mapProducts = Regex.Matches(MapOnlyDataJson, productsPattern);

                            var listyList = mapProducts.Cast<Match>().Select(match => match.Groups[1].ToString()).ToList();
                            MapOnlyDataJson = string.Join(",", listyList);

                            string productAndPriceJson = "{\"productList\": [" + MapOnlyDataJson + "], \"prices\":[" + cleanedPriceJson + "]}";
                            var task = Task.Run(async () =>
                            {
                                await SaveRawData(query, productAndPriceJson, "Favorites");
                            });
                            await this.AddTaskAndRun(task);
                        }
                    }

                }

            }
            catch (Exception ex)
            {
                logger.Error("NewSysco Raw Data Category {0} Failed: {1}, {2}, {3}", "Favorites", ex.Message, ex.InnerException, ex.StackTrace);
                var task = Task.Run(async () =>
                {
                    await SaveBlankData(this.Query, "Favorites");
                });
                await this.AddTaskAndRun(task);
            }

            return true;
        }

        private async Task<bool> isSyncStillRunning(GetProductsQuery query)
        {
            bool isStillRunning = await Mediator.HandleAsync(new SyncStillRunningQuery
            {
                RequestId = query.RequestId,
            });

            return isStillRunning;
        }
    }
}
