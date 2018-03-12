﻿using CoralTime.Common.Exceptions;
using CoralTime.Common.Helpers;
using CoralTime.DAL.ConvertModelToView;
using CoralTime.DAL.Models;
using CoralTime.ViewModels.Reports;
using CoralTime.ViewModels.Reports.Request.Grid;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using static CoralTime.Common.Constants.Constants;

namespace CoralTime.BL.Services.Reports.DropDownsAndGrid
{
    public partial class ReportsService
    {
        private int GroupById { get; set; }

        private int[] ShowColumnIds { get; set; }

        #region Get DropDowns and Grid. Filtration By / Grouping By: Projects, Users, Dates, Clients.

        public ReportTotalView GetReportsGroupingBy(ReportsGridView reportsGridView)
        {
            _reportsSettingsService.SaveCurrentQuery(reportsGridView.CurrentQuery);

            GroupById = SetGroupByOrDefaultGrouping(reportsGridView.CurrentQuery.GroupById);
            ShowColumnIds = reportsGridView.CurrentQuery.ShowColumnIds;

            var answer = new ReportTotalView();

            var filteredTimeEntries = GetFilteredTimeEntries(reportsGridView);
            if (filteredTimeEntries.Any())
            {
                switch (reportsGridView.CurrentQuery.GroupById)
                {
                    case (int) ReportsGroupBy.Project:
                    {
                        var timeEntriesGroupByProjects = filteredTimeEntries
                            .GroupBy(i => i.Project)
                            .OrderBy(x => x.Key.Name)
                            .ToDictionary(key => key.Key, value => value.OrderBy(x => x.Date).ToList());

                        return answer.GetView(timeEntriesGroupByProjects, GroupById, ShowColumnIds);
                    }

                    case (int) ReportsGroupBy.Member:
                    {
                        var timeEntriesGroupByMembers = filteredTimeEntries
                            .GroupBy(i => i.Member)
                            .OrderBy(x => x.Key.FullName)
                            .ToDictionary(key => key.Key, value => value.OrderBy(x => x.Date).ToList());

                        return answer.GetView(timeEntriesGroupByMembers, GroupById, ShowColumnIds);
                    }

                    case (int) ReportsGroupBy.Date:
                    {
                        var timeEntriesGroupByDate = filteredTimeEntries
                            .GroupBy(i => i.Date)
                            .OrderBy(x => x.Key)
                            .ToDictionary(key => key.Key, key => key.OrderBy(x => x.Date).ToList());

                        return answer.GetView(timeEntriesGroupByDate, GroupById, ShowColumnIds);
                    }

                    case (int) ReportsGroupBy.Client:
                    {
                        var timeEntriesGroupByClients = filteredTimeEntries
                            .GroupBy(i => i.Project.Client == null ? CreateWithOutClientInstance() : i.Project.Client)
                            .OrderBy(x => x.Key.Name)
                            .ToDictionary(key => key.Key, value => value.OrderBy(x => x.Date).ToList());

                        return answer.GetView(timeEntriesGroupByClients, GroupById, ShowColumnIds);
                    }
                }
            }

            return answer;
        }

        #endregion

        #region Get DropDowns and Grid. Filtration By / Grouping By: None, Projects, Users, Dates, Clients. (Common methods)

        private List<TimeEntry> GetFilteredTimeEntries(ReportsGridView reportsGridData)
        {
            var currentMember = Uow.MemberRepository.LinkedCacheGetByName(InpersonatedUserName);

            if (currentMember == null)
            {
                throw new CoralTimeEntityNotFoundException($"Member with userName = {InpersonatedUserName} not found.");
            }

            if (!currentMember.User.IsActive)
            {
                throw new CoralTimeEntityNotFoundException($"Member with userName = {InpersonatedUserName} is not active.");
            }

            CommonHelpers.SetRangeOfWeekByDate(out var weekStart, out var weekEnd, DateTime.Now);

            DateFrom = reportsGridData.CurrentQuery?.DateFrom ?? weekStart;
            DateTo = reportsGridData.CurrentQuery?.DateTo ?? weekEnd;

            // By Dates (default grouping, i.e. "Group by None"; direct order).
            var timeEntriesByDateOfUser = GetTimeEntryByDate(currentMember, DateFrom, DateTo);

            // By Projects.
            if (reportsGridData.CurrentQuery?.ProjectIds != null && reportsGridData.CurrentQuery.ProjectIds.Length > 0)
            {
                CheckAndSetIfInFilterChooseSingleProject(reportsGridData, timeEntriesByDateOfUser);

                timeEntriesByDateOfUser = timeEntriesByDateOfUser.Where(x => reportsGridData.CurrentQuery.ProjectIds.Contains(x.ProjectId));
            }

            // By Members.
            if (reportsGridData.CurrentQuery?.MemberIds != null && reportsGridData.CurrentQuery.MemberIds.Length > 0)
            {
                timeEntriesByDateOfUser = timeEntriesByDateOfUser.Where(x => reportsGridData.CurrentQuery.MemberIds.Contains(x.MemberId));
            }

            // By Clients that has Projects.
            if (reportsGridData.CurrentQuery?.ClientIds != null && reportsGridData.CurrentQuery.ClientIds.Length > 0)
            {
                timeEntriesByDateOfUser = timeEntriesByDateOfUser.Where(x => reportsGridData.CurrentQuery.ClientIds.Contains(x.Project.ClientId) || x.Project.ClientId == null && reportsGridData.CurrentQuery.ClientIds.Contains(WithoutClient.Id));
            }

            return timeEntriesByDateOfUser.ToList();
        }

        private void CheckAndSetIfInFilterChooseSingleProject(ReportsGridView reportsGridData, IQueryable<TimeEntry> timeEntriesByDateOfUser)
        {
            if (reportsGridData.CurrentQuery.ProjectIds.Length == 1)
            {
                var singleFilteredProjectId = reportsGridData.CurrentQuery.ProjectIds.FirstOrDefault();
                SingleFilteredProjectName = Uow.ProjectRepository.LinkedCacheGetById(singleFilteredProjectId).Name;
            }
        }

        private IQueryable<TimeEntry> GetTimeEntryByDate(Member currentMember, DateTime dateFrom, DateTime dateTo)
        {
            // #0 Get timeEntriesByDate.s
            var timeEntriesByDate = Uow.TimeEntryRepository.GetQueryWithIncludes()
                .Include(x => x.Project).ThenInclude(x => x.Client)
                .Include(x => x.Member.User)
                .Include(x => x.TaskType)
                .Where(t => t.Date.Date >= dateFrom.Date && t.Date.Date <= dateTo.Date);

            #region Constrain for Admin: return all TimeEntries.

            if (currentMember.User.IsAdmin)
            {
                return timeEntriesByDate;
            }

            #endregion

            #region Constrain for Member. return only TimeEntries that manager is assign.

            if (!currentMember.User.IsAdmin && !currentMember.User.IsManager)
            {
                // #1. TimeEntries. Get tEntries for this member.
                timeEntriesByDate = timeEntriesByDate.Where(t => t.MemberId == currentMember.Id);
            }

            #endregion

            #region Constrain for Manager : return #1 TimeEntries that currentMember is assign, #2 TimeEntries for not assign users at Projects (but TEntries was saved), #4 TimeEntries with global projects that not contains in result.

            if (!currentMember.User.IsAdmin && currentMember.User.IsManager)
            {
                var managerRoleId = Uow.ProjectRoleRepository.LinkedCacheGetList().FirstOrDefault(r => r.Name == ProjectRoleManager).Id;

                var managerProjectIds = Uow.MemberProjectRoleRepository.LinkedCacheGetList()
                    .Where(r => r.MemberId == currentMember.Id && r.RoleId == managerRoleId)
                    .Select(x => x.ProjectId)
                    .ToArray();

                // #1. TimeEntries. Get tEntries for this member and tEntries that is current member is Manager!.
                timeEntriesByDate = timeEntriesByDate.Where(t => t.MemberId == currentMember.Id || managerProjectIds.Contains(t.ProjectId));
            }

            return timeEntriesByDate;

            #endregion
        }

        private Client CreateWithOutClientInstance()
        {
            return new Client
            {
                Id = WithoutClient.Id,
                Name = WithoutClient.Name,
                CreationDate = DateTime.Now,
                LastUpdateDate = DateTime.Now,
            };
        }

        #endregion

        // TODO dublicate int reportsExportService
        private int SetGroupByOrDefaultGrouping(int? groupById)
        {
            return groupById ?? (int) ReportsGroupBy.Date;
        }
    }
}